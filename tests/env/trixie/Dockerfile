FROM debian:trixie
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    ffmpeg xvfb imagemagick x11-utils iproute2 procps curl xz-utils ca-certificates python3 default-jre-headless libavahi-client3 avahi-daemon avahi-utils dbus \
 && rm -rf /var/lib/apt/lists/*
# FFmpeg 8.1 shared libs (avutil 60) for the offset test, kept out of the default path
RUN curl -fsSL https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-linux64-gpl-shared-8.1.tar.xz | tar -xJ -C /opt \
 && mv /opt/ffmpeg-n8.1-* /opt/ff8
