#!/bin/bash
# vMix PC stand-in: stock bars with noise -> omtx out (x264), link shaped by tc on eth0
export PATH=/dist:$PATH LD_LIBRARY_PATH=/dist
omtx bars --noise --fps 30 --size 1280x720 > /tmp/bars.log 2>&1 &
sleep 1
omtx out omt://127.0.0.1:6400 --encoder libx264 --stats 2>&1 | sed -u "s/^/[$(date +%s)] /" &
sleep 14
echo "=== $(date +%T) throttle eth0 to 4 Mbit/s"
tc qdisc add dev eth0 root tbf rate 4mbit burst 16kb latency 100ms
sleep 20
echo "=== $(date +%T) release throttle"
tc qdisc del dev eth0 root
sleep 25
