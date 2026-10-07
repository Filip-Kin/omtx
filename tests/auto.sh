#!/bin/bash
# VMIXPC side of the automatic bridge test: a stock OMT "vMix - Output 1" and omtx ui (auto both ways).
source /t/mdns.sh
omtx bars --name "vMix - Output 1" --size 1280x720 --fps 30 > /tmp/vmix.log 2>&1 &
omtx ui --no-open > /tmp/ui.log 2>&1 &
st() { curl -s localhost:6390/api/state | python3 -c '
import json,sys; d=json.load(sys.stdin)
for b in d["bridges"]:
  for s in b.get("streams") or []: print(" ", b["kind"], s["source"], "->", s["publishedAs"], (s["stats"] or {}).get("state"), "fps", (s["stats"] or {}).get("fps"), "rx", (s["stats"] or {}).get("receivers"))'; }
sleep 8; echo "=== no viewers"; st
echo "=== probe the omtx side of vMix - Output 1"
omtx probe "VMIXPC (vMix - Output 1 omtx)" --seconds 6 | tail -2 & P=$!
sleep 4; st; wait $P
echo "=== probe the OMT side of the phone"
omtx probe "VMIXPC (PHONE1 Camera)" --seconds 6 | tail -2 & P=$!
sleep 4; st; wait $P
sleep 7; echo "=== 7 s after the viewers left"; st
echo "=== ui log"; cat /tmp/ui.log
