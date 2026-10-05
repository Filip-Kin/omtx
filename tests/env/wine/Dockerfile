FROM debian:trixie
RUN dpkg --add-architecture i386 && apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends wine wine64 libwine ca-certificates procps xvfb \
 && rm -rf /var/lib/apt/lists/*
ENV WINEDEBUG=-all WINEPREFIX=/root/.wine
RUN wineboot -i >/dev/null 2>&1 || true
