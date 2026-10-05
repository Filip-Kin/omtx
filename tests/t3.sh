#!/bin/bash
# Kotlin sender (Android omtx-core) -> C# omtx play and omtx in -> omtx out -> play
set -u
export PATH=/dist:$PATH LD_LIBRARY_PATH=/dist
Xvfb :99 -screen 0 1280x720x24 >/dev/null 2>&1 &
sleep 1; export DISPLAY=:99
java -jar /apk/omtx-file-sender.jar /clips/$1 30 1280 720 6500 > /tmp/fs.log 2>&1 &
sleep 3
omtx in omtx://127.0.0.1:6500 --stats > /tmp/in.log 2>&1 &          # stock on 6400
sleep 1
omtx out omt://127.0.0.1:6400 --encoder libx264 > /tmp/out.log 2>&1 &   # omtx on 6401
sleep 1
SDL_AUDIODRIVER=dummy omtx play omtx://127.0.0.1:6500 --window 640x360+0+0 --stats > /tmp/play1.log 2>&1 &
SDL_AUDIODRIVER=dummy omtx play omtx://127.0.0.1:6401 --window 640x360+640+0 --stats > /tmp/play2.log 2>&1 &
sleep 12
import -window root /shots/t3-$2.png
for f in fs in out play1 play2; do echo "--- $f"; tail -5 /tmp/$f.log; done
