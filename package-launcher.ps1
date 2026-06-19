<#
  package-launcher.ps1 — build a distributable "BAMP Manager" (the launcher / updater).

  WHAT IT DOES
    1. Publishes the launcher as a SELF-CONTAINED, single-file Windows x64 .exe
       (players need nothing pre-installed — no .NET runtime).
    2. Carries launcher-settings.json + assets next to the .exe (the launcher reads
       them from its own folder at runtime).
    3. Defensively strips any real launcher-secrets.json from the output so a webhook
       can never be shipped by accident — only the .example template travels.
    4. Zips it to dist\BAMP-Manager-<version>.zip, nested under a "BAMP Manager\"
       folder, ready to attach to a GitHub Release.

  The launcher is VERSION-INDEPENDENT from the mod: it reads the latest GitHub Release
  at runtime, so it only needs re-publishing when the launcher ITSELF changes — not
  every mod update.

  BUG-REPORT WEBHOOK (optional, not baked in)
    To enable Discord auto-upload later, drop a launcher-secrets.json (copy
    launcher-secrets.example.json and fill in the URL) next to the .exe, OR set the
    BAMP_BUG_REPORT_WEBHOOK environment variable. NEVER commit the real secret.

  USAGE
    .\package-launcher.ps1                 # version defaults to 1.0.0
    .\package-launcher.ps1 -Version 1.1.0
#>
[CmdletBinding()]
param([string]$Version = "1.0.0")

$ErrorActionPreference = 'Stop'
$root  = $PSScriptRoot
$proj  = Join-Path $root "tools\BigAmbitionsMP.Launcher\BigAmbitionsMP.Launcher.csproj"
$dist  = Join-Path $root "dist"
$stage = Join-Path $dist "_stage\BAMP Manager"

# ── 1. Publish (self-contained single-file win-x64) ──────────────────────────
Write-Host "==> Publishing BAMP Manager $Version (self-contained win-x64) ..." -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null

dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:Version=$Version -o $stage | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed." }

# ── 2. Safety: never ship a real secrets file ────────────────────────────────
$realSecret = Join-Path $stage "launcher-secrets.json"
if (Test-Path $realSecret) {
    Remove-Item -Force $realSecret
    Write-Host "    (removed a stray launcher-secrets.json from the output — secrets never ship)" -ForegroundColor Yellow
}

# ── 3. Zip (nested under "BAMP Manager\") ────────────────────────────────────
New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist "BAMP-Manager-$Version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $stage -DestinationPath $zip
Remove-Item -Recurse -Force (Join-Path $dist "_stage")

# ── 4. Report ────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host ("==> Packaged: {0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Cyan
Write-Host "    Contents:"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try { $archive.Entries | ForEach-Object { Write-Host ("      " + $_.FullName) } }
finally { $archive.Dispose() }
