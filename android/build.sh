#!/usr/bin/env bash
# Build in Docker and copy the APK and jar to dist/. Nothing else lands on the host.
set -euo pipefail
cd "$(dirname "$0")"
DOCKER_BUILDKIT=1 docker build -t omtx-android-build .
mkdir -p dist
cid=$(docker create omtx-android-build)
trap 'docker rm "$cid" >/dev/null' EXIT
docker cp "$cid":/out/omtx-camera.apk dist/omtx-camera.apk
docker cp "$cid":/out/omtx-file-sender.jar dist/omtx-file-sender.jar
ls -l dist
