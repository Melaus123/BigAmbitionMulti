@echo off
:: Spawned by launch-mp-test.bat.  Sets the autopilot env var + Steam
:: env vars (so SteamAPI_RestartAppIfNecessary doesn't relaunch via
:: Steam and discard our BAMP_AUTOROLE in the process), then starts
:: the HOST instance (Steam install).
::
:: Same Steam-env trick that C:\BigAmbitions2\launch_client.bat uses.
set BAMP_AUTOROLE=host
set SteamAppId=1331550
set SteamGameId=1331550
set SteamOverlayGameId=1331550
start "" "C:\Program Files (x86)\Steam\steamapps\common\Big Ambitions\Big Ambitions.exe" -monitor 1
