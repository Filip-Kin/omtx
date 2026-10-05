#!/bin/bash
set -u
export PATH=/dist:$PATH LD_LIBRARY_PATH=/dist
Xvfb :99 -screen 0 1280x720x24 >/dev/null 2>&1 &
sleep 1; export DISPLAY=:99
FPS=${1:-30}
# 1) one hop: omtx bars (x264) -> omtx play
omtx bars --omtx --fps $FPS --size 1280x720 > /tmp/b1.log 2>&1 & B=$!
sleep 1
SDL_AUDIODRIVER=dummy omtx play omtx://127.0.0.1:6400 > /tmp/p1.log 2>&1 & P=$!
sleep 5
python3 /t/latency.py 20 "omtx bars -> play ($FPS fps)"
kill $P $B; sleep 2; pkill ffplay; sleep 1
# 2) vMix path: stock VMX bars -> omtx out (x264) -> omtx play
omtx bars --fps $FPS --size 1280x720 > /tmp/b2.log 2>&1 &
sleep 1
omtx out omt://127.0.0.1:6400 --encoder libx264 > /tmp/o2.log 2>&1 &
sleep 1
SDL_AUDIODRIVER=dummy omtx play omtx://127.0.0.1:6401 > /tmp/p2.log 2>&1 &
sleep 5
python3 /t/latency.py 20 "stock bars -> omtx out -> play ($FPS fps)"
import -window root /shots/t4.png
