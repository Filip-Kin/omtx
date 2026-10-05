#!/bin/bash
# $1 = label, $2 = extra LD_LIBRARY_PATH for a specific FFmpeg
set -u
export PATH=/dist:$PATH LD_LIBRARY_PATH="${2:+$2:}/dist"
Xvfb :99 -screen 0 1280x720x24 >/dev/null 2>&1 &
sleep 1; export DISPLAY=:99
omtx bars --name STOCK --fps 30 --size 1280x720 > /tmp/bars.log 2>&1 &          # 6400 stock VMX
sleep 1
omtx out omt://127.0.0.1:6400 --encoder libx264 --stats > /tmp/out1.log 2>&1 &   # 6401 omtx
sleep 1
omtx in omtx://127.0.0.1:6401 --stats > /tmp/in.log 2>&1 &                       # 6402 stock again
sleep 1
omtx out omt://127.0.0.1:6402 --encoder libx264 --name OUT2 > /tmp/out2.log 2>&1 &  # 6403 omtx
sleep 1
SDL_AUDIODRIVER=dummy omtx play omtx://127.0.0.1:6403 --stats > /tmp/play.log 2>&1 &
sleep 12
import -window root /shots/t2-$1.png
for f in bars out1 in out2 play; do echo "--- $f"; cat /tmp/$f.log; done
