#!/bin/bash
set -u
export PATH=/dist:$PATH LD_LIBRARY_PATH=/dist
Xvfb :99 -screen 0 1280x720x24 >/dev/null 2>&1 &
sleep 1; export DISPLAY=:99
omtx bars --omtx --name T1 --fps 30 --size 1280x720 > /tmp/bars.log 2>&1 &
sleep 2
SDL_AUDIODRIVER=dummy omtx play omtx://127.0.0.1:6400 high --stats > /tmp/play.log 2>&1 &
sleep 8
import -window root /shots/t1.png
pgrep -a ffplay | cut -c1-200
echo "--- bars"; cat /tmp/bars.log; echo "--- play"; cat /tmp/play.log
