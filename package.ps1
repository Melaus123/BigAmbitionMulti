<#
  package.ps1 — build a distributable zip of BigAmbitionsMP for players.

  WHAT IT DOES
    1. Builds the mod FRESH (so it controls exactly what gets packaged).
    2. Reads the build marker the compiler embedded in the DLL.
    3. Refuses to ship a DEV build by mistake (unless you pass -AllowDev).
    4. Gathers the runtime files (DLL + Dependencies + assets) in mod-folder layout.
    5. Zips them into dist\BigAmbitionsMP-<version>[-DEV].zip, ready to upload to a
       GitHub Release or Thunderstore.

  USAGE
    .\package.ps1            Build Release (clean, no diagnostics) and zip it. The
                            normal "cut a public release" command.
    .\package.ps1 -AllowDev  Build Dev (diagnostics on) instead and zip it as
                            ...-DEV.zip — for deliberately handing a tester a
                            diagnostic build.  Never upload a -DEV zip publicly.
#>
[CmdletBinding()]
param([switch]$AllowDev)

$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot                                   # the repo folder (where this script lives)
$config = if ($AllowDev) { 'Dev' } else { 'Release' }

# ── 1. Build fresh ────────────────────────────────────────────────────────────
Write-Host "==> Building -c $config ..." -ForegroundColor Cyan
dotnet build "$root\BigAmbitionsMP.csproj" -c $config | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed (config '$config')." }

$outDir = Join-Path $root "bin\$config\net48"
$dll    = Join-Path $outDir "BigAmbitionsMP.dll"
if (-not (Test-Path $dll)) { throw "Built DLL not found at $dll" }

# ── 2. Read the build marker out of the DLL ──────────────────────────────────
# .NET stores string literals as UTF-16 inside the DLL, so we scan the raw bytes
# for the UTF-16 form of "DEV build" (which only a Dev build's BuildTag contains).
function Test-DllContainsText([string]$path, [string]$text) {
    $hay    = [IO.File]::ReadAllBytes($path)
    $needle = [Text.Encoding]::Unicode.GetBytes($text)
    for ($i = 0; $i -le $hay.Length - $needle.Length; $i++) {
        $ok = $true
        for ($j = 0; $j -lt $needle.Length; $j++) {
            if ($hay[$i + $j] -ne $needle[$j]) { $ok = $false; break }
        }
        if ($ok) { return $true }
    }
    return $false
}

$dllIsDev = Test-DllContainsText $dll "DEV build"
Write-Host ("==> DLL build marker: {0}" -f $(if ($dllIsDev) { 'DEV' } else { 'release' })) -ForegroundColor Green

# ── 3. Safety stop ───────────────────────────────────────────────────────────
if ($dllIsDev -and -not $AllowDev) {
    throw "SAFETY STOP: the built DLL is marked a DEV build (diagnostics compiled in), but you are packaging a public release. If the build config is wired correctly this should never happen. Re-run with -AllowDev only if you deliberately want a diagnostic build."
}

# ── 4. Version (single source of truth: the .cs constant) ─────────────────────
$srcText = Get-Content (Join-Path $root "src\MyPluginInfo.cs") -Raw
if ($srcText -notmatch 'PLUGIN_VERSION\s*=\s*"([^"]+)"') { throw "Could not read PLUGIN_VERSION from MyPluginInfo.cs." }
$version = $Matches[1]

# ── 5. Stage the runtime files in mod-folder layout ──────────────────────────
$dist  = Join-Path $root "dist"
$stage = Join-Path $dist "_stage"
$modDir = Join-Path $stage "BigAmbitionsMP"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force (Join-Path $modDir "Dependencies") | Out-Null

Copy-Item $dll                                        $modDir
Copy-Item (Join-Path $outDir "0Harmony.dll")          (Join-Path $modDir "Dependencies")
Copy-Item (Join-Path $outDir "LiteNetLib.dll")        (Join-Path $modDir "Dependencies")
Copy-Item (Join-Path $root "assets\BAMP_ChatIcon.png") $modDir
Copy-Item (Join-Path $root "assets\BAMP_HubIcon.png")  $modDir
# MIT requires the bundled Harmony / LiteNetLib license notices to travel with them.
Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.txt")  $modDir
Copy-Item (Join-Path $root "LICENSE")                  $modDir

# ── 6. Zip it ────────────────────────────────────────────────────────────────
$suffix = if ($dllIsDev) { "-DEV" } else { "" }
$zip = Join-Path $dist "BigAmbitionsMP-$version$suffix.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $modDir -DestinationPath $zip
Remove-Item -Recurse -Force $stage

# ── 7. Report ────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host ("==> Packaged: {0}  ({1:N0} KB)" -f $zip, ((Get-Item $zip).Length / 1KB)) -ForegroundColor Cyan
Write-Host "    Contents:"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try { $archive.Entries | ForEach-Object { Write-Host ("      " + $_.FullName) } }
finally { $archive.Dispose() }
