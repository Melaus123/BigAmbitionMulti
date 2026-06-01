@echo off
:: Spawned by launch-mp-test.bat.  Sets the autopilot env var and calls
:: the existing client launcher (which sets Steam env vars before
:: starting the second instance).
set BAMP_AUTOROLE=client
call "C:\BigAmbitions2\launch_client.bat"
