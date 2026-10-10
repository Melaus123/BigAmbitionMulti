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
total_errors = 0
for filename in FILES:
    payload = (ROOT / filename).read_bytes()
    tree = parser.parse(payload)
    errors = []
    stack = [tree.root_node]
    while stack:
        node = stack.pop()
        if node.type == "ERROR" or node.is_missing:
            errors.append(node)
        if node.has_error:
            stack.extend(reversed(node.children))
    total_errors += len(errors)
    print(f"{filename}: {len(errors)} syntax errors")
    for e in errors[:5]:
        print(f"  {e.type} at lines {e.start_point.row + 1}-{e.end_point.row + 1}")
if total_errors:
    sys.exit(f"Found {total_errors} C# syntax errors")
print("Issue #8 C# syntax parse: PASS")
