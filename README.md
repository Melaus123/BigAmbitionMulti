<p align="center">
  <img src="media/logo.png" alt="Going Public — Multiplayer for Big Ambitions" width="420">
</p>

<h1 align="center">Going Public</h1>
<p align="center"><b>Multiplayer for Big Ambitions</b></p>

---

> **Beta release.** This is an early public beta — expect bugs. **Back up your single-player saves before using it.** Reporting problems is the whole point of this release; see [Reporting bugs](#reporting-bugs).

**Going Public** adds multiplayer to [Big Ambitions](https://store.steampowered.com/app/1331550/Big_Ambitions/). One player hosts a world and others join over the network to play in the same city — shopping in each other's stores, building competing business empires, and appearing on each other's rivals leaderboard, with a synchronized clock, shared world state, vehicles, and more.

## Requirements

- **Big Ambitions**, Early Access **0.11** (the Mono / "experimental" branch). The mod is built against 0.11.
- **Both players must run the same game version and the same mod version.** The mod checks this when you connect and refuses a mismatch (so an out-of-date build can't quietly desync a session).
- Uses the game's built-in mod loader — no third-party loader required.

## Installation

1. Download the `.zip` from the [releases page](https://github.com/Melaus123/BigAmbitionMulti/releases).
2. Extract it — you'll get a `BigAmbitionsMP` folder.
3. Copy that `BigAmbitionsMP` folder into your local mods folder:
   ```
   %USERPROFILE%\AppData\LocalLow\Hovgaard Games\Big Ambitions\ModsLocal\
   ```
   You should end up with `...\ModsLocal\BigAmbitionsMP\BigAmbitionsMP.dll`.
4. Launch the game, open the **Mods** menu, and enable **BigAmbitionsMP**.

Every player installs the same way.

## Hosting and joining

- One player **hosts**; the others **join** by entering the host's IP address.
- The default port is **7777 (UDP)**. For internet play the host needs that port reachable — forward it on the router, or use a LAN / VPN tool (Radmin, ZeroTier, Hamachi, etc.). On the same local network, the host's LAN IP works directly.

## Reporting bugs

This beta exists to find bugs, so please report them. [Open an issue](https://github.com/Melaus123/BigAmbitionMulti/issues) with:

- What happened and what you expected.
- Whether you were the **host** or a **client**.
- The log file from **both** machines:
  ```
  %USERPROFILE%\AppData\LocalLow\Hovgaard Games\Big Ambitions\Player.log
  ```

In multiplayer, click the `Report` button in the in-game chat window to open a bug report form. Use `Attach files` to include screenshots, short videos, logs, or other useful files. Files over 24 MB are kept out of the Discord upload so one large video does not make the whole report fail.

In multiplayer chat, you can also type `/bug what happened here`. The mod creates a report folder under:

```
%USERPROFILE%\AppData\LocalLow\Hovgaard Games\Big Ambitions\ModsLocal\BigAmbitionsMP\bug-reports\
```

To send reports to Discord automatically, add `BugReportDiscordWebhookUrl` to the mod config JSON with your Discord webhook URL. The webhook is never hardcoded in the mod. Discord uploads include the player's description, `Player.log`, `Player-prev.log` when present, `bamp-ring.log`, and selected attachments; the local report folder keeps a fuller set for deeper investigation.

For Discord forum channels with required tags, configure the forum tag IDs in the mod config JSON:

```
"BugReportDiscordCrashTagId": "123456789012345678",
"BugReportDiscordBugTags": "Bugs=123456789012345679;Performance=123456789012345680;Desync=123456789012345681"
```

Crash reports apply the crash tag automatically. Manual bug reports show a `Type` button in the report popup so the player can choose one of the configured bug tags before sending.

For local testing only, `AllowBugReportCrashTest=true` enables `/bugcrash confirm reason`. This intentionally closes the game so the next launch can show the crash-report popup.

## Building from source

Requires the .NET SDK and a local Big Ambitions install (the project references the game's own assemblies). Build the shipping mod with `dotnet build -c Release`; run `package.ps1` to produce a distributable zip. A `dotnet build -c Dev` build re-enables developer/diagnostic tooling that the shipped build leaves out.

## License & credits

- Released under the [MIT License](LICENSE) — you're free to use, modify, and build your own versions on top of it.
- Bundles [Harmony](https://github.com/pardeike/Harmony) and [LiteNetLib](https://github.com/RevenantX/LiteNetLib), both under the MIT License (see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)).
- Big Ambitions is a game by Hovgaard Games. This is an unofficial, fan-made mod and is not affiliated with or endorsed by Hovgaard Games.
