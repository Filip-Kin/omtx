#!/bin/bash
source /t/mdns.sh
Xvfb :99 -screen 0 1280x720x24 >/dev/null 2>&1 &
export DISPLAY=:99
sleep 3
echo "=== omtx list"; omtx list --seconds 4
echo "=== play by name"; omtx play "PHONE1 (Camera)" --stats > /tmp/play.log 2>&1 &
echo "=== omtx in (no arguments)"; omtx in --stats > /tmp/in.log 2>&1 &
sleep 10
import -window root /shots/t9.png
echo "=== omtx list after bridge"; omtx list --seconds 3
echo "=== what stock OMT software browses (_omt._tcp)"; avahi-browse -tp _omt._tcp | grep -v "^+;.*;IPv6" | cut -d';' -f4 | sort -u
echo "=== play log"; cat /tmp/play.log; echo "=== in log"; cat /tmp/in.log
