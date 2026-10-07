#!/usr/bin/env bash
# Build in Docker and copy the APK and jar to dist/. Nothing else lands on the host.
set -euo pipefail
cd "$(dirname "$0")"
# Signing key kept outside the repo and outside Docker. The first build makes one (Gradle's debug
# key) and saves it here; every later build signs with it, so updates install over each other.
KEY="${OMTX_ANDROID_KEYSTORE:-$HOME/.config/omtx/android-debug.keystore}"
secret=()
[ -s "$KEY" ] && secret=(--secret "id=signing,src=$KEY")
DOCKER_BUILDKIT=1 docker build "${secret[@]}" -t omtx-android-build .
mkdir -p dist
cid=$(docker create omtx-android-build)
trap 'docker rm "$cid" >/dev/null' EXIT
if [ ! -s "$KEY" ]; then
  mkdir -p "$(dirname "$KEY")"
  docker cp "$cid":/out/debug.keystore "$KEY" && chmod 600 "$KEY"
  echo "signing key saved to $KEY"
fi
docker cp "$cid":/out/omtx-camera.apk dist/omtx-camera.apk
docker cp "$cid":/out/omtx-file-sender.jar dist/omtx-file-sender.jar
ls -l dist
