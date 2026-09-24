"""tools/report_stub.py - a local stand-in for the bug-report relay (H-REPORTLOSS-1, 2026-09-23).

Rig tests point BugReportRelayUrl at this stub so no test report can ever reach the real relay.
It listens on 127.0.0.1 only, saves every request body it receives, and answers according to a mode:

  ok          every POST gets 200
  fail N      the first N POSTs get 503, every later one 200
  stall       reads the body, then never answers (the client's timeout is what ends it)
  413         every POST gets 413 (payload too large)
  429 [S]     every POST gets 429 with "Retry-After: S" (default 30; e.g. `429 90`)

Usage:
  python tools/report_stub.py <port> <mode> [N|S] [--out DIR]

Per POST it writes into DIR (default: %TEMP%\\report-stub-<port>):
  post-<n>.bin          the raw request body
  post-<n>-<file>.zip   each uploaded .zip file part, extracted from the multipart body
  stub.log              one line per request: number, time, bytes, answer, and for each zip whether
                        it opens, its entry count, how many .hsg entries it holds, and whether
                        bundle-index.txt and Player.log are inside
GET / answers with the current counters (a health check). Stop it with Ctrl+C or by killing it.
"""
import datetime
import io
import os
import re
import sys
import tempfile
import threading
import time
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

STATE = {"n": 0, "lock": threading.Lock()}


def log(out, line):
    stamp = datetime.datetime.utcnow().strftime("%Y-%m-%d %H:%M:%S")
    text = "%sZ %s" % (stamp, line)
    print(text, flush=True)
    with open(os.path.join(out, "stub.log"), "a", encoding="utf-8") as f:
        f.write(text + "\n")


def zip_parts(body, content_type):
    """(filename, bytes) for every multipart file part whose filename ends in .zip."""
    m = re.search(r"boundary=([^;]+)", content_type or "")
    if not m:
        return []
    boundary = ("--" + m.group(1).strip().strip('"')).encode("ascii", "replace")
    found = []
    for chunk in body.split(boundary):
        head, sep, data = chunk.partition(b"\r\n\r\n")
        if not sep:
            continue
        fm = re.search(rb'filename="([^"]+)"', head)
        if not fm or not fm.group(1).lower().endswith(b".zip"):
            continue
        if data.endswith(b"\r\n"):
            data = data[:-2]
        found.append((fm.group(1).decode("utf-8", "replace"), data))
    return found


def describe_zip(data):
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as z:
            bad = z.testzip()
            names = z.namelist()
            hsg = [n for n in names if n.lower().endswith(".hsg")]
            return "opens=%s entries=%d hsg=%d index=%s playerlog=%s" % (
                bad is None, len(names), len(hsg), "bundle-index.txt" in names, "Player.log" in names)
    except Exception as ex:
        return "opens=False error=%s" % ex


def make_handler(mode, count, out):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def _answer(self, code, extra=None):
            payload = b'{"ok":%s}' % (b"true" if code < 300 else b"false")
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            for k, v in (extra or {}).items():
                self.send_header(k, v)
            self.end_headers()
            self.wfile.write(payload)

        def do_GET(self):
            self._answer(200)

        def do_POST(self):
            with STATE["lock"]:
                STATE["n"] += 1
                n = STATE["n"]
            length = int(self.headers.get("Content-Length") or 0)
            body = b""
            while len(body) < length:
                chunk = self.rfile.read(min(1 << 20, length - len(body)))
                if not chunk:
                    break
                body += chunk
            with open(os.path.join(out, "post-%d.bin" % n), "wb") as f:
                f.write(body)
            notes = []
            for name, data in zip_parts(body, self.headers.get("Content-Type")):
                with open(os.path.join(out, "post-%d-%s" % (n, os.path.basename(name))), "wb") as f:
                    f.write(data)
                notes.append("%s %s" % (name, describe_zip(data)))
            if mode == "ok":
                code, extra = 200, None
            elif mode == "fail":
                code, extra = (503, None) if n <= count else (200, None)
            elif mode == "413":
                code, extra = 413, None
            elif mode == "429":
                code, extra = 429, {"Retry-After": str(count)}
            else:   # stall
                code, extra = 0, None
            log(out, "post %d bytes=%d got=%d answer=%s key=%s | %s" % (
                n, length, len(body), code or "stall", "yes" if self.headers.get("X-BAMP-Key") else "no",
                "; ".join(notes) or "no zip part"))
            if code == 0:
                time.sleep(3600)
                return
            self._answer(code, extra)

    return Handler


def main():
    args = sys.argv[1:]
    out = None
    if "--out" in args:
        i = args.index("--out")
        out = args[i + 1]
        del args[i:i + 2]
    if len(args) < 2 or args[1] not in ("ok", "fail", "stall", "413", "429"):
        sys.exit(__doc__)
    port, mode = int(args[0]), args[1]
    if mode == "fail":
        count = int(args[2]) if len(args) > 2 else 1
    elif mode == "429":
        count = int(args[2]) if len(args) > 2 else 30    # the Retry-After seconds
    else:
        count = 0
    out = out or os.path.join(tempfile.gettempdir(), "report-stub-%d" % port)
    os.makedirs(out, exist_ok=True)
    srv = ThreadingHTTPServer(("127.0.0.1", port), make_handler(mode, count, out))
    srv.daemon_threads = True
    log(out, "report stub listening on http://127.0.0.1:%d/ mode=%s%s, saving to %s" % (
        port, mode, (" %d" % count) if mode in ("fail", "429") else "", out))
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
