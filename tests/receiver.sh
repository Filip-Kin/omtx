#!/bin/bash
export PATH=/dist:$PATH LD_LIBRARY_PATH=/dist
Xvfb :99 -screen 0 1280x720x24 >/dev/null 2>&1 &
sleep 1; export DISPLAY=:99
omtx play omtx://$1:6401 --stats 2>&1 | sed -u "s/^/[play] /" &
sleep 10; python3 /t/latency.py 8 "before throttle"
sleep 8;  python3 /t/latency.py 8 "during throttle"; import -window root /shots/t8-throttled.png
sleep 18; python3 /t/latency.py 8 "after release"; import -window root /shots/t8-after.png
sleep 5
