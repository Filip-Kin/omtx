#!/bin/bash
# Regression run in Docker, no LAN traffic (an --internal network for the mDNS and throttle tests).
#   tests/run.sh            after: DOCKER_BUILDKIT=1 docker build -f build/Dockerfile --output type=local,dest=dist/build .
# Screenshots land in tests/shots/. Each test prints its own numbers; read them.
set -u
T="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(dirname "$T")"
L="$ROOT/dist/build/linux-x64"; W="$ROOT/dist/build/win-x64"; A="$ROOT/android/dist"; S="$T/shots"
mkdir -p "$S"
docker build -q -t omtx-test-env "$T/env/trixie" >/dev/null
docker build -q -t omtx-test-bullseye "$T/env/bullseye" >/dev/null
docker build -q -t omtx-wine "$T/env/wine" >/dev/null
docker network create --internal omtx-net >/dev/null 2>&1 || true
M="-v $L:/dist:ro -v $T:/t:ro -v $S:/shots"
echo "== chain (stock VMX -> out -> in -> out -> play) on FFmpeg 4.3, 7.1, 8.1"
docker run --rm $M omtx-test-bullseye timeout 60 /t/t2.sh ff43 '' | grep -E "play\]"
docker run --rm $M omtx-test-env timeout 60 /t/t2.sh ff71 '' | grep -E "play\]"
docker run --rm $M omtx-test-env timeout 60 /t/t2.sh ff81 /opt/ff8/lib | grep -E "play\]"
echo "== latency (clock strip read back from ffplay screenshots)"
docker run --rm $M omtx-test-env timeout 90 /t/t4.sh 30 | grep latency
docker run --rm $M omtx-test-env timeout 90 /t/t4.sh 60 | grep latency
if [ -f "$A/omtx-file-sender.jar" ] && [ -f "$T/clips/test.h264" ]; then
  echo "== Android omtx-core sender -> C# play/in/out"
  docker run --rm $M -v "$T/clips:/clips:ro" -v "$A:/apk:ro" omtx-test-env timeout 60 /t/t3.sh test.h264 h264 | grep -E "play\]"
fi
echo "== throttle (4 Mbit/s on the sender link)"
docker rm -f omtx-snd >/dev/null 2>&1
docker run -d --name omtx-snd --network omtx-net --cap-add NET_ADMIN -v $L:/dist:ro -v $T:/t:ro omtx-test-env timeout 80 /t/sender.sh >/dev/null
sleep 2; IP=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' omtx-snd)
docker run --rm --network omtx-net $M omtx-test-env timeout 75 /t/receiver.sh "$IP" | grep latency
docker logs omtx-snd 2>&1 | grep -E "\[out\] [0-9]|===" | sed 's/^\[[0-9]*\] //'; docker rm -f omtx-snd >/dev/null
echo "== mDNS discovery (two containers)"
docker rm -f omtx-phone >/dev/null 2>&1
docker run -d --name omtx-phone --hostname PHONE1 --network omtx-net -v $L:/dist:ro -v $T:/t:ro omtx-test-env timeout 60 /t/phone.sh >/dev/null
docker run --rm --hostname VMIXPC --network omtx-net $M omtx-test-env timeout 50 /t/vmixpc.sh | grep -v cuvid
docker rm -f omtx-phone >/dev/null
echo "== automatic bridges (idle until watched, both directions)"
docker run -d --name omtx-phone --hostname PHONE1 --network omtx-net -v $L:/dist:ro -v $T:/t:ro omtx-test-env bash -c "source /t/mdns.sh; omtx bars --omtx --name Camera --size 1280x720 --fps 30 2>&1" >/dev/null
docker run --rm --hostname VMIXPC --network omtx-net -v $L:/dist:ro -v $T:/t:ro omtx-test-env timeout 60 /t/auto.sh 2>&1 | grep -E "^===|^  " | grep -v "ui log"
docker rm -f omtx-phone >/dev/null
echo "== Windows build under Wine"
docker run --rm -v $W:/win:ro -v $T:/t:ro omtx-wine timeout 120 /t/wine-chain.sh 2>&1 | grep -E "^\[|^---"
