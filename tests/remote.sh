#!/bin/bash
export PATH=/dist:$PATH LD_LIBRARY_PATH=/dist
Xvfb :99 -screen 0 1920x1080x24 >/dev/null 2>&1 & sleep 1; export DISPLAY=:99
omtx play omtx://100.96.142.61:6401 --stats > /tmp/play.log 2>&1 &
sleep 18; import -window root /shots/remote.png; cat /tmp/play.log
