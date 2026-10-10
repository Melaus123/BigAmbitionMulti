# Issue #8 — merged company HQ quantity regression

Original report: https://github.com/Melaus123/BigAmbitionMulti/issues/8

## Automated checks

```powershell
python -m unittest discover -s tools/tests -p "test_issue8_*.py" -v
```

This checks source-level invariants and validates the two-player scenario. It runs on
GitHub Actions, without the game or proprietary assemblies.

On the Windows development rig, after building and deploying the same `Dev`
DLL to both instances and preparing the **save1** fixture:

```powershell
python tools/regress.py t-issue8-quantities
```

The scenario tests 14 -> 25 warehouse target units and 31 -> 47 imported
units, both directions, and reads actual native plan targets with
`hqlog <planId> targets`. It requires a stocked single-token SKU in
the owner's warehouse and importer product list, plus an active logistics
plan with a destination and a truck/driver. The probe is read-only.

## Manual Unity UI test (not covered by the headless rig)

1. Both players enter the same merged company. Player A owns an HQ, a
   warehouse with a driver/truck, and an importer partnership; B opens A's HQ.
2. A sets an existing warehouse destination's daily quantity to 14. B should
   see 14 after the owner publishes.
3. B focuses that row's number input and types **25 slowly**, one digit at
   a time. Allow several company-list publishes while the field is focused.
   The pane must stay open and the input must keep focus/content.
4. B commits 25. A should see 25; B should see 25 after the echo. Reopen the
   pane: both views must still agree.
5. Repeat from A -> B and B -> A on an importer product amount (31 -> 47).
6. Repeat while B has a dropdown open, with another HQ tab open on A, and
   during a company-list refresh. Neither a zero nor a partial value may win.
7. Remove a logistics destination, add another, unmerge/remerge, disconnect,
   and restart both players. Check no duplicate plans or orphaned rows.
8. Verify non-merged local plans still edit natively, and that an absent
   owner's *simulated* real plan still executes once (no double deliveries).

## Logs to inspect

Expect routed `logistics destset` and `purchasing target` edits to be
applied exactly once on the authoritative owner, with urgent publication.
Look for `stale partner purchasing row ... refused`, `display logistics
reconcile`, or `plan edit REFUSED`: investigate if they appear during
a normal quantity edit.

## Environment limitations

The net48 mod references Steam/Unity and Big Ambitions game assemblies.
A Linux GitHub Actions runner cannot build or launch the real game.
The static tests are *not* substitutes for a two-player runtime smoke test.
Do not mark upstream issue #8 fixed solely from the static CI result.
