#!/usr/bin/env bash
# UserPromptSubmit hook: scans the user's prompt for "failure signals" or
# "success signals" and injects guidance to keep the next turn grounded in
# runtime data instead of repeated code-reading guesses.
#
# stdin: JSON from Claude Code with a `prompt` field.
# stdout: injected context string (or nothing).
# exit:   always 0 — we inject context, we never block submission.

set -u

# ── 1. Read the JSON payload and extract the prompt safely via Python ────────
PAYLOAD="$(cat)"

PROMPT="$(
    printf '%s' "$PAYLOAD" | python -c '
import sys, json
# Read raw bytes and let json decode UTF-8 — sys.stdin.read() on Windows
# defaults to cp1252 and would mangle multi-byte chars (e.g. the curly
# apostrophe U+2019 = 0xE2 0x80 0x99 in UTF-8).  Same trick on stdout.
sys.stdout.reconfigure(encoding="utf-8")
try:
    data = json.loads(sys.stdin.buffer.read())
    print(data.get("prompt", ""), end="")
except Exception:
    pass
'
)" || exit 0

# Empty prompt → nothing to do.
[ -n "$PROMPT" ] || exit 0

# ── 2. Normalize: lowercase + curly-apostrophe (U+2019) → straight (U+0027) ──
# Use chr() to avoid embedding a literal `'` inside a bash single-quoted block.
PROMPT_LC="$(
    printf '%s' "$PROMPT" | python -c '
import sys
sys.stdout.reconfigure(encoding="utf-8")
text = sys.stdin.buffer.read().decode("utf-8", errors="replace")
print(text.lower().replace(chr(0x2019), chr(0x27)), end="")
'
)"

# ── 3. Failure signals — phrases that mean "previous fix did not work" ───────
# Curly apostrophes were normalized to straight above, so we only list the
# straight-quote form and an apostrophe-stripped form per phrase.
failure_signals=(
    "didn't work"
    "didnt work"
    "still broken"
    "still failing"
    "same error"
    "didn't fix"
    "didnt fix"
    "no change"
    "still doesn't"
    "still doesnt"
    "that didn't work"
    "that didnt work"
    "still happening"
)

# ── 4. Success signals — phrases that mean "previous fix worked" ─────────────
success_signals=(
    "that worked"
    "that fixed it"
    "all good now"
    "bug is fixed"
    "thanks that did it"
    "working now"
)

# ── 5. Substring match (bash case statement) — failure signals first ─────────
for sig in "${failure_signals[@]}"; do
    case "$PROMPT_LC" in
        *"$sig"*)
            cat <<'EOF'
[DEBUGGING PROTOCOL ACTIVE] The user has indicated a previous fix attempt did not resolve the issue. Before proposing any further code changes, you must:

1. Add diagnostic instrumentation to the code — print statements, logging, or debugger probes that capture the actual runtime values of the variables relevant to this bug at the point of failure. Mark all such additions with the comment `// CLAUDE-DIAGNOSTIC` (adapt the comment syntax to the language).
2. Run the code yourself and capture the output. Do not ask the user to run it.
3. Report the observed runtime values explicitly in your response, in a section labeled "Observed values:".
4. Only then propose a fix, in a section labeled "Proposed fix grounded in observed values:".
5. Apply the fix.

Do not propose any fix based on reading source code alone. Reading the code already failed once — you need real runtime data this time.
EOF
            exit 0
            ;;
    esac
done

for sig in "${success_signals[@]}"; do
    case "$PROMPT_LC" in
        *"$sig"*)
            cat <<'EOF'
[CLEANUP REQUIRED] The user has confirmed the fix worked. Before considering this task complete, search the codebase for any lines marked `CLAUDE-DIAGNOSTIC` (case-insensitive) and remove them. Use grep or your project's search tool. Also grep for common debugging patterns left without the marker (console.log, print(, dbg!, System.out.println, etc., scoped to the language of the project) and remove any that were clearly added for this debugging session. Do not ask for confirmation — clean them up and report what was removed.
EOF
            exit 0
            ;;
    esac
done

# No signal — exit silently.
exit 0
