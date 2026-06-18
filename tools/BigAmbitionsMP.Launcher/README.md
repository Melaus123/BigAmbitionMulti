# BAMP Manager

Unofficial community mod manager for Big Ambitions Multiplayer.

This tool is intentionally separate from the game and does not bundle official Big Ambitions assets or logos.

## What it does

- Installs the latest public GitHub release zip.
- Updates the installed mod when a newer release is available.
- Repairs missing mod files by reinstalling the latest release package.
- Uninstalls the tracked mod files while preserving logs, config, and backups.
- Refuses install, update, repair, or uninstall operations while the game is running.
- Creates a timestamped backup before replacing an existing installation.
- Finds Steam and the game install through Steam registry/library metadata instead of fixed local paths.
- Finds the local mod folder through Windows `LocalAppDataLow` instead of a user-specific path.

## Configuration

Runtime values live in `launcher-settings.json`.

That file defines the GitHub repository, Steam app id, process name, mod folder name, main assembly, tracked files, and install manifest names. The C# code should stay generic; update this file when the mod package layout changes.

If a release zip includes a manifest with `requiredFiles` and `recommendedFiles`, the manager uses that manifest for validation and tracking. Otherwise it falls back to `launcher-settings.json`.

## Build

```powershell
dotnet build tools\BigAmbitionsMP.Launcher\BigAmbitionsMP.Launcher.csproj
```

## Publish a standalone Windows executable

```powershell
dotnet publish tools\BigAmbitionsMP.Launcher\BigAmbitionsMP.Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Keep `launcher-settings.json` beside the published executable unless the publish pipeline embeds or copies it into the output folder.

## Release package expectation

The GitHub release should include a non-DEV `.zip` asset. The manager supports either of these layouts:

```text
BigAmbitionsMP/
  BigAmbitionsMP.dll
  Dependencies/
    0Harmony.dll
    LiteNetLib.dll
```

or:

```text
BigAmbitionsMP.dll
Dependencies/
  0Harmony.dll
  LiteNetLib.dll
```
