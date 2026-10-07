@echo off
rem Republishes every omtx camera (phone) on the network as a normal OMT source.
rem In vMix: Add Input > OMT > "<this PC> (<phone> Camera)".
"%~dp0omtx.exe" in --stats
pause
