# syntax=docker/dockerfile:1.7
# Builds the omtx camera APK and the omtx-file-sender jar entirely in-container (the host
# has no Android SDK). Runs the omtx-core JVM tests first; a failing test fails the build.
# SDK lines follow Projects/RightNow/Dockerfile.android, minus node/bun/NDK.
#
#   ./build.sh            -> dist/omtx-camera.apk, dist/omtx-file-sender.jar
#
# Gradle caches live in a BuildKit cache mount, so rebuilds are fast. The signing key comes from
# the host (build.sh passes it as a build secret): BuildKit garbage-collects cache mounts, and a
# key kept only there was lost once (2026-10-07), so the next APK would not install over the
# last one. Build output stays inside the container; only /out is copied to the host.
FROM eclipse-temurin:17-jdk-jammy AS build

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
      curl unzip ca-certificates && rm -rf /var/lib/apt/lists/*

ENV ANDROID_HOME=/opt/android-sdk
ENV ANDROID_SDK_ROOT=${ANDROID_HOME}
ENV PATH="${ANDROID_HOME}/cmdline-tools/latest/bin:${ANDROID_HOME}/platform-tools:${PATH}"
RUN mkdir -p ${ANDROID_HOME}/cmdline-tools \
      && curl -fsSL https://dl.google.com/android/repository/commandlinetools-linux-11076708_latest.zip -o /tmp/cmdtools.zip \
      && unzip -q /tmp/cmdtools.zip -d ${ANDROID_HOME}/cmdline-tools \
      && mv ${ANDROID_HOME}/cmdline-tools/cmdline-tools ${ANDROID_HOME}/cmdline-tools/latest \
      && rm /tmp/cmdtools.zip
RUN yes | sdkmanager --licenses >/dev/null \
      && sdkmanager --install "platforms;android-35" "build-tools;35.0.0" >/dev/null

ENV GRADLE_VERSION=8.11.1
RUN curl -fsSL https://services.gradle.org/distributions/gradle-${GRADLE_VERSION}-bin.zip -o /tmp/gradle.zip \
      && unzip -q /tmp/gradle.zip -d /opt \
      && rm /tmp/gradle.zip
ENV PATH="/opt/gradle-${GRADLE_VERSION}/bin:${PATH}"

WORKDIR /src
COPY . .

RUN --mount=type=cache,target=/root/.gradle \
    --mount=type=secret,id=signing \
      mkdir -p /root/.android /out \
      && if [ -s /run/secrets/signing ]; then cp /run/secrets/signing /root/.android/debug.keystore; fi \
      && gradle --no-daemon --console=plain \
        :omtx-core:test :omtx-core:fileSenderJar :app:assembleRelease \
      && cp /root/.android/debug.keystore /out/debug.keystore \
      && cp app/build/outputs/apk/release/app-release.apk /out/omtx-camera.apk \
      && cp omtx-core/build/dist/omtx-file-sender.jar /out/omtx-file-sender.jar
