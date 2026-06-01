@echo off
:: ════════════════════════════════════════════════════════════════════
:: BigAmbitionsMP — testing aid launcher
:: ════════════════════════════════════════════════════════════════════
:: Starts BOTH game instances with BAMP_AUTOROLE pre-set so the mod's
:: autopilot drives host/connect/start/customizer-confirm with no manual
:: clicks.  No-op in normal play (env var unset = autopilot disabled).
::
:: Press F2 in either game window to abort that instance's autopilot.
:: ════════════════════════════════════════════════════════════════════

start "" cmd /c ""%~dp0_launch_host_internal.bat""
start "" cmd /c ""%~dp0_launch_client_internal.bat""
