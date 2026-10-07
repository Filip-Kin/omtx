#!/bin/bash
cp -r /win /root/omtx; cd /root/omtx
wine omtx.exe --version 2>&1 | tail -1
W="wine omtx.exe"
# stock VMX bars -> omtx out (libx264 from the bundled FFmpeg 8.1) -> omtx in -> (stock) ; omtx play needs ffplay, skipped
$W bars --name STOCK --size 1280x720 --fps 30 > /tmp/bars.log 2>&1 &
sleep 4
$W out omt://127.0.0.1:6400 --encoder h264_nvenc,libx264 --stats > /tmp/out.log 2>&1 &
sleep 4
$W in omtx://127.0.0.1:6401 --stats > /tmp/in.log 2>&1 &
sleep 4
$W out omt://127.0.0.1:6402 --encoder libx264 --name OUT2 --stats > /tmp/out2.log 2>&1 &
sleep 4
$W in omtx://127.0.0.1:6403 --stats > /tmp/in2.log 2>&1 &
sleep 4
# every bridge works only while watched: this viewer at the end wakes the whole chain
$W probe omt://127.0.0.1:6404 --seconds 14 > /tmp/probe.log 2>&1
for f in bars out in out2 in2 probe; do echo "--- $f"; tail -4 /tmp/$f.log; done
