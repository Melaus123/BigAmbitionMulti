# sync-test-rig.ps1 — keep the two-instance test rig on IDENTICAL game content.
#
# WHY THIS EXISTS (2026-07-26): instance 2 runs from a SEPARATE install
# (C:\BigAmbitions2), and it silently fell ~1 month behind the Steam install after a
# game content update. Item data differed between the two, which produced a host-vs-client
# "difference" that looked exactly like a mod bug. Four rounds of investigation went into
# what was really a stale copy. Run -Check before any host-vs-client test session.
#
#   .\sync-test-rig.ps1 -Check    # verify only; exit 1 if the installs differ
#   .\sync-test-rig.ps1           # verify, then sync Steam -> copy if needed
#
# Preserves instance-2-only files (launch_client.bat, temp\) and its BepInEx setup.

[CmdletBinding()]
param(
    [switch]$Check,
    [string]$Source = "C:\Program Files (x86)\Steam\steamapps\common\Big Ambitions",
    [string]$Target = "C:\BigAmbitions2"
)

$ErrorActionPreference = "Stop"

foreach ($p in @($Source, $Target)) {
    if (-not (Test-Path $p)) { Write-Host "MISSING install: $p" -ForegroundColor Red; exit 2 }
}

# Compare the files that carry game CONTENT. Name+size is enough to catch a content
# update; timestamps alone give false alarms after a re-copy.
function Get-ContentDiff {
    param($SrcRoot, $DstRoot)
    $diff = @()
    foreach ($sub in @("Big Ambitions_Data", "")) {
        $s = if ($sub) { Join-Path $SrcRoot $sub } else { $SrcRoot }
        $d = if ($sub) { Join-Path $DstRoot $sub } else { $DstRoot }
        if (-not (Test-Path $s)) { continue }
        foreach ($f in Get-ChildItem $s -File) {
            if ($f.Name -in @("launch_client.bat")) { continue }
            $t = Join-Path $d $f.Name
            if (-not (Test-Path $t)) { $diff += "$sub\$($f.Name) : missing in copy" }
            elseif ((Get-Item $t).Length -ne $f.Length) { $diff += "$sub\$($f.Name) : size differs" }
        }
    }
    return $diff
}

$running = Get-Process | Where-Object { $_.ProcessName -like "*Big*Ambitions*" }
$diff = Get-ContentDiff $Source $Target

if ($diff.Count -eq 0) {
    Write-Host "RIG OK - both installs carry identical game content." -ForegroundColor Green
    exit 0
}

Write-Host "RIG MISMATCH - $($diff.Count) file(s) differ between the installs:" -ForegroundColor Yellow
$diff | Select-Object -First 10 | ForEach-Object { Write-Host "   $_" }
if ($diff.Count -gt 10) { Write-Host "   ... and $($diff.Count - 10) more" }

if ($Check) {
    Write-Host "`nHost-vs-client results from this rig are NOT trustworthy until synced." -ForegroundColor Red
    Write-Host "Run:  .\sync-test-rig.ps1" -ForegroundColor Red
    exit 1
}

if ($running) {
    Write-Host "`nClose the game first - it is running (PID(s): $($running.Id -join ', '))." -ForegroundColor Red
    exit 3
}

Write-Host "`nSyncing game content (BepInEx / temp / launch_client.bat preserved)..." -ForegroundColor Cyan
robocopy $Source $Target /E /XD "BepInEx" "temp" /XF "launch_client.bat" /R:1 /W:1 /NFL /NDL /NJH /NP | Out-Null
if ($LASTEXITCODE -ge 8) { Write-Host "robocopy FAILED (exit $LASTEXITCODE)" -ForegroundColor Red; exit 4 }

$after = Get-ContentDiff $Source $Target
if ($after.Count -eq 0) { Write-Host "SYNC OK - installs now match." -ForegroundColor Green; exit 0 }
Write-Host "STILL DIFFERENT after sync ($($after.Count) file(s)) - investigate." -ForegroundColor Red
exit 5
