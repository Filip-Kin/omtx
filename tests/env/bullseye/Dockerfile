FROM debian:bullseye
RUN printf "deb http://archive.debian.org/debian bullseye main\ndeb http://archive.debian.org/debian-security bullseye-security main\n" > /etc/apt/sources.list \
 && apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    ffmpeg xvfb imagemagick procps iproute2 libavahi-client3 \
 && rm -rf /var/lib/apt/lists/*
