"""Parse the four issue-8 touched C# files without game binaries.

Requires: pip install tree-sitter tree-sitter-c-sharp
This catches syntax mistakes; it does NOT typecheck or run the Unity mod.
"""
from pathlib import Path
import sys
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

# Tree-sitter C# currently reports one false positive in the project's
# pre-existing nested interpolated HQ diagnostic (unchanged from main).
# Parsing the massive mod files twice in the same process also crashes a
# tree-sitter native extension version on hosted CI; parse each file ONCE.
KNOWN_PREEXISTING = lambda filename, err: (
    filename == "src/CompanyPlans.cs"
    and err[0] == "ERROR"
    and err[1].startswith('+ $"purchasing:')
    and "headhunter:" in err[1]
)

total_new = 0
for filename in FILES:
    payload = (ROOT / filename).read_bytes()
    found = error_signatures(payload)
    new_errors = [err for err in found if not KNOWN_PREEXISTING(filename, err)]
    total_new += len(new_errors)
    print(f"{filename}: {len(new_errors)} NEW syntax errors ({len(found)} total)", flush=True)
    for kind, line, col, line_no in new_errors[:5]:
        print(f"  {kind} at {line_no}:{col} -> {line[:100]}", flush=True)
if total_new:
    sys.exit(f"Found {total_new} new C# syntax errors")
print("Issue #8 C# syntax parse: PASS")
