"""patch_compat_check.py - after a GAME UPDATE: check every Harmony target and every reflected member name
the mod uses against a decompiled game tree, and list the misses.

Usage: python tools/patch_compat_check.py <decompile-src-dir> [<old-decompile-src-dir>]
  e.g. python tools/patch_compat_check.py C:\\code\\cpp2il\\mono-1.0-update0916\\src C:\\code\\cpp2il\\mono-1.0-update0902\\src

What it checks (mechanically; a miss is a CLAIM for a read to judge, not a verdict):
  1. [HarmonyPatch(typeof(<Type>), "<method>" | nameof(<Type>.<method>) ...)]  -> the type's .cs exists in the tree and
     declares a member named <method> (any overload; constructors/getters/setters recognised by the MethodType arg).
  2. AccessTools.Field/Method/Property(typeof(<Type>), "<name>") and typeof(<Type>).GetField/GetMethod/GetProperty("<name>")
     -> the type declares <name>.
  3. Harmony injection args `___fieldName` in patch signatures -> the patched type declares <fieldName>.
Types are located by their simple name across the tree (namespace folders directly under src); an ambiguous simple
name is reported as such.  With the old tree given, each miss says whether the member existed there (a REMOVAL) or
never did (a false positive of this script).
"""
import os, re, sys, glob, collections

SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src")
NEW = sys.argv[1]
OLD = sys.argv[2] if len(sys.argv) > 2 else None

def index_tree(root):
    """simple type name -> list of file paths declaring it (class/struct/interface/enum)."""
    idx = collections.defaultdict(list)
    decl = re.compile(r"^\s*(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|\s)*\s*(?:class|struct|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)", re.M)
    for f in glob.glob(os.path.join(root, "**", "*.cs"), recursive=True):
        try: txt = open(f, encoding="utf-8", errors="ignore").read()
        except Exception: continue
        for m in decl.finditer(txt):
            idx[m.group(1)].append(f)
    return idx

def member_declared(files, name, kind):
    """Does any of these files declare a member `name`? kind: method|field|prop|any|ctor"""
    pats = []
    n = re.escape(name)
    if kind in ("method", "any"):
        pats.append(re.compile(r"\b" + n + r"\s*(<[^>]*>)?\s*\("))
    if kind in ("field", "prop", "any"):
        pats.append(re.compile(r"[\w<>\[\],\.\?]+\s+" + n + r"\s*(;|=|\{|\n)"))
    if kind == "ctor":
        return True
    for f in files:
        txt = open(f, encoding="utf-8", errors="ignore").read()
        for p in pats:
            if p.search(txt): return True
    return False

def simple(tname):
    return tname.split(".")[-1].strip()

def overloads(files, name):
    """How many DECLARATIONS of a method named `name` the type's files hold (a modifier before the name)."""
    n = 0
    pat = re.compile(r"(?:public|private|protected|internal)\s+(?:static\s+|virtual\s+|override\s+|async\s+|unsafe\s+|new\s+)*[\w<>\[\],\.\?]+\s+" + re.escape(name) + r"\s*(<[^>]*>)?\s*\(")
    for f in files:
        n += len(pat.findall(open(f, encoding="utf-8", errors="ignore").read()))
    return n

def has_arg_types(attr_text):
    return "new Type[" in attr_text or "new[]" in attr_text or "typeof(" in attr_text.split(")", 1)[-1] or "argumentTypes" in attr_text or "MethodType" in attr_text

where_src = {}

harmony = re.compile(r"\[HarmonyPatch\(\s*typeof\(([\w\.]+)\)\s*(?:,\s*(?:\"([^\"]+)\"|nameof\(([\w\.]+)\)))?([^\]]*)\]", re.S)
access  = re.compile(r"AccessTools\.(Field|Method|Property|DeclaredMethod|DeclaredField|DeclaredProperty)\(\s*typeof\(([\w\.]+)\)\s*,\s*\"([^\"]+)\"")
getx    = re.compile(r"typeof\(([\w\.]+)\)\s*\.Get(Field|Method|Property)\(\s*\"([^\"]+)\"")
inject  = re.compile(r"\[HarmonyPatch\(\s*typeof\(([\w\.]+)\)[^\]]*\][\s\S]{0,600}?static\s+\w[\w<>\[\],\.\?]*\s+(?:Prefix|Postfix|Finalizer|Transpiler)\s*\(([^)]*)\)")

def main():
    new = index_tree(NEW); old = index_tree(OLD) if OLD else None
    checks = []   # (kind, type, member, file:line)
    for f in glob.glob(os.path.join(SRC, "*.cs")):
        txt = open(f, encoding="utf-8", errors="ignore").read()
        base = os.path.basename(f)
        def line_of(pos): return txt.count("\n", 0, pos) + 1
        for m in harmony.finditer(txt):
            t, ms, mn, rest = m.group(1), m.group(2), m.group(3), m.group(4) or ""
            member = ms or (mn.split(".")[-1] if mn else None)
            kind = "method"
            if "MethodType.Constructor" in rest: kind = "ctor"
            elif "MethodType.Getter" in rest or "MethodType.Setter" in rest: kind = "prop"
            if member is None and kind != "ctor":
                continue   # class-level attribute with the method on a nested attribute - skip
            where = f"{base}:{line_of(m.start())}"; where_src[where] = m.group(0)
            checks.append((("harmony", kind), t, member or "<ctor>", where))
        for m in access.finditer(txt):
            what, t, member = m.group(1), m.group(2), m.group(3)
            kind = "method" if "Method" in what else ("prop" if "Property" in what else "field")
            checks.append((("reflect", kind), t, member, f"{base}:{line_of(m.start())}"))
        for m in getx.finditer(txt):
            t, what, member = m.group(1), m.group(2), m.group(3)
            kind = "method" if what == "Method" else ("prop" if what == "Property" else "field")
            checks.append((("reflect", kind), t, member, f"{base}:{line_of(m.start())}"))
        for m in inject.finditer(txt):
            t, params = m.group(1), m.group(2)
            for pm in re.finditer(r"___([A-Za-z_][A-Za-z0-9_]*)", params):
                checks.append((("inject", "field"), t, pm.group(1), f"{base}:{line_of(m.start())}"))
    misses = []; ambiguous = set(); ok = 0
    def pick(idx, tname):
        """Files declaring the simple name; when several, prefer the one whose namespace folder matches the
        typeof's namespace (a game type and a plugin type may share a simple name - 2026-09-16: the game's
        VehicleController vs NWH.VehiclePhysics2.VehicleController produced a bogus 1,136-line 'diff')."""
        st = simple(tname); files = idx.get(st, [])
        ns = tname.rsplit(".", 1)[0] if "." in tname else ""
        if len(files) > 1 and ns:
            narrowed = [f for f in files if os.sep + ns + os.sep in f or "/" + ns + "/" in f]
            if narrowed: return narrowed
        return files
    for (src, kind), t, member, where in checks:
        st = simple(t)
        files = pick(new, t)
        if not files:
            misses.append((src, kind, t, member, where, "TYPE NOT FOUND")); continue
        if len(files) > 1: ambiguous.add(st)
        if member == "<ctor>" or member_declared(files, member, kind):
            # 2026-09-16 (read A): a Harmony attribute WITHOUT argument types on a method that now has
            # several overloads throws AmbiguousMatchException at patch time and binds arbitrarily.
            if src == "harmony" and kind == "method" and "argTypes" not in where and overloads(files, member) > 1 and not has_arg_types(where_src.get(where, "")):
                misses.append((src, kind, t, member, where, f"AMBIGUOUS: {overloads(files, member)} overloads, attribute names no argument types")); continue
            ok += 1; continue
        was = ""
        if old is not None:
            ofiles = pick(old, t)
            was = "REMOVED (was in old)" if ofiles and member_declared(ofiles, member, kind) else "not in old either (script false positive?)"
        misses.append((src, kind, t, member, where, "MEMBER NOT FOUND " + was))
    print(f"checks: {len(checks)}  ok: {ok}  misses: {len(misses)}  ambiguous simple names: {len(ambiguous)}")
    for src, kind, t, member, where, why in sorted(misses, key=lambda x: (x[5], x[2], x[3])):
        print(f"  MISS [{src}/{kind}] {t}.{member}  @ {where}  -> {why}")
    if ambiguous:
        print("ambiguous (checked across all same-named types): " + ", ".join(sorted(ambiguous)))

if __name__ == "__main__":
    main()
