@echo off
rem vMix: Settings > Outputs / NDI / SRT > Output 1 > OMT on.
rem Finds this PC's vMix OMT output by itself (Output 1 first) and sends it as H.264.
rem To pick a different one: omtx.exe out "PCNAME (Source Name)"   (omtx-list.cmd shows the names)
"%~dp0omtx.exe" out --stats
pause
