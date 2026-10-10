"""Parse the four issue-8 touched C# files without game binaries.

Requires: pip install tree-sitter tree-sitter-c-sharp
This catches syntax mistakes; it does NOT typecheck or run the Unity mod.
"""
from pathlib import Path
import sys
import subprocess
import tree_sitter
import tree_sitter_c_sharp

ROOT = Path(__file__).resolve().parents[2]
FILES = ("src/PaperworkSync.cs", "src/MergerAbsence.cs",
         "src/CompanyPlans.cs", "src/MPPatches.cs")

parser = tree_sitter.Parser(tree_sitter.Language(tree_sitter_c_sharp.language()))

def error_signatures(payload):
    tree = parser.parse(payload)
    lines = payload.decode("utf-8-sig", errors="replace").splitlines()
    errors = []
    stack = [tree.root_node]
    while stack:
        node = stack.pop()
        if node.type == "ERROR" or node.is_missing:
            line = lines[node.start_point.row].strip() if node.start_point.row < len(lines) else ""
            errors.append((node.type, line, node.start_point.column, node.start_point.row + 1))
        if node.has_error:
            stack.extend(reversed(node.children))
    return errors

total_new = 0
for filename in FILES:
    payload = (ROOT / filename).read_bytes()
    current = error_signatures(payload)
    baseline = subprocess.run(["git", "show", f"origin/main:{filename}"],
                              cwd=ROOT, capture_output=True)
    if baseline.returncode:
        sys.exit(f"Cannot compare syntax to main for {filename}: {baseline.stderr.decode(errors='replace')}")
    known = {(kind, line, col) for kind, line, col, _ in error_signatures(baseline.stdout)}
    new_errors = [err for err in current if err[:3] not in known]
    total_new += len(new_errors)
    print(f"{filename}: {len(new_errors)} NEW syntax errors ({len(current)} total)")
    for kind, line, col, line_no in new_errors[:5]:
        print(f"  {kind} at {line_no}:{col} -> {line[:100]}")
if total_new:
    sys.exit(f"Found {total_new} new C# syntax errors")
print("Issue #8 C# syntax parse vs main: PASS")
