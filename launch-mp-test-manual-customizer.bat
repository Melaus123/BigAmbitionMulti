@echo off
:: ════════════════════════════════════════════════════════════════════
:: BigAmbitionsMP — manual-customizer testing aid
:: ════════════════════════════════════════════════════════════════════
:: Same as launch-mp-test.bat (autopilot drives host/connect/start) but
:: PAUSES at the character creator on each instance so you can type a
:: custom in-character name and verify the name-flow plumbing.
::
:: Press Continue in each character creator manually.  Once both are
:: done the autopilot's WaitCustomizer state detects the customizer
:: GameObject disappearing and transitions to Done as usual.
::
:: Press F2 in either game window to abort that instance's autopilot.
:: ════════════════════════════════════════════════════════════════════

:: Pass the manual flag via a wrapper that sets it then chains the
:: existing internal launchers.  Keeps the original launchers intact
:: for normal autopilot runs.

start "" cmd /c "set BAMP_MANUAL_CUSTOMIZER=1 && call ""%~dp0_launch_host_internal.bat"""
start "" cmd /c "set BAMP_MANUAL_CUSTOMIZER=1 && call ""%~dp0_launch_client_internal.bat"""
