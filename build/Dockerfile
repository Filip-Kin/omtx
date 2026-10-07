# Builds every omtx desktop artefact. From the repo root:
#
#   DOCKER_BUILDKIT=1 docker build -f build/Dockerfile --output type=local,dest=dist .
#
# dist/linux-x64/  omtx (NativeAOT), libvmx.so, omtx-play-wrapper
#                  Built on Debian 11 (glibc 2.31) so it runs on Debian 11, 12 and 13.
#                  Uses the system FFmpeg (4.x to 8.x) and ffplay; the projector client
#                  only needs `omtx play`, which uses neither libvmx nor libav.
# dist/win-x64/    omtx.exe (self-contained single file), libvmx.dll, FFmpeg 8.1 DLLs

# ---- Linux: NativeAOT on bullseye (same base as ndi-build/omt.Dockerfile) ----
FROM debian:bullseye AS linux
RUN printf "deb http://archive.debian.org/debian bullseye main\ndeb http://archive.debian.org/debian-security bullseye-security main\n" > /etc/apt/sources.list \
 && apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    ca-certificates curl git clang lld zlib1g-dev libicu-dev \
 && rm -rf /var/lib/apt/lists/*
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/di.sh \
 && bash /tmp/di.sh --channel 8.0 --install-dir /opt/dotnet
ENV PATH=/opt/dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# libvmx (stock OMT codec), pinned like ndi-build. Only the AVX2 file gets AVX2, so CPUs
# without it fall back to SSE at runtime instead of crashing.
ARG LIBVMX_REV=a1828cb
RUN git clone https://github.com/openmediatransport/libvmx.git /vmx && git -C /vmx checkout $LIBVMX_REV \
 && mkdir -p /out/linux-x64 && cd /vmx/src \
 && clang++ -O3 -std=c++17 -fdeclspec -fPIC -msse4.2 -mssse3 -mlzcnt -c vmxcodec.cpp      -o /tmp/vmx.o \
 && clang++ -O3 -std=c++17 -fdeclspec -fPIC -msse4.2 -mssse3 -mlzcnt -c vmxcodec_x86.cpp  -o /tmp/vmx_x86.o \
 && clang++ -O3 -std=c++17 -fdeclspec -fPIC -mavx2 -mbmi -mbmi2 -mlzcnt -c vmxcodec_avx2.cpp -o /tmp/vmx_avx2.o \
 && clang++ -shared -fPIC /tmp/vmx.o /tmp/vmx_x86.o /tmp/vmx_avx2.o -o /out/linux-x64/libvmx.so

WORKDIR /src
COPY libomtnet/ libomtnet/
COPY tools/ tools/
RUN --mount=type=cache,target=/root/.nuget/packages \
    cd tools/omtx && dotnet publish -c Release -r linux-x64 -o /tmp/pub \
 && cp /tmp/pub/omtx /out/linux-x64/omtx && strip /out/linux-x64/omtx || true
COPY build/omtx-play-wrapper /out/linux-x64/omtx-play-wrapper
RUN chmod 755 /out/linux-x64/omtx /out/linux-x64/omtx-play-wrapper

# ---- Windows: self-contained single file ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS windows
RUN apt-get update && apt-get install -y --no-install-recommends unzip && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY libomtnet/ libomtnet/
COPY tools/ tools/
RUN --mount=type=cache,target=/root/.nuget/packages \
    cd tools/omtx && dotnet publish -c Release -r win-x64 -o /tmp/pub \
 && mkdir -p /out/win-x64 && cp /tmp/pub/omtx.exe /out/win-x64/
# libvmx.dll from the upstream OMT binaries release
ARG OMT_RELEASE=v1.0.0.19
RUN curl -fsSL -o /tmp/omt.zip https://github.com/openmediatransport/libomtnet/releases/download/${OMT_RELEASE}/OpenMediaTransport.Binaries.Release.${OMT_RELEASE}.zip \
 && unzip -q /tmp/omt.zip -d /tmp/omt \
 && cp /tmp/omt/Libraries/Winx64/libvmx.dll /out/win-x64/
# FFmpeg 8.1 shared (GPL: includes libx264/libx265 as software fallbacks; NVENC/NVDEC/QSV/AMF built in)
ARG FFMPEG_ZIP=ffmpeg-n8.1-latest-win64-gpl-shared-8.1
RUN curl -fsSL -o /tmp/ff.zip https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/${FFMPEG_ZIP}.zip \
 && unzip -q /tmp/ff.zip -d /tmp/ff \
 && cp /tmp/ff/${FFMPEG_ZIP}/bin/avcodec-*.dll /tmp/ff/${FFMPEG_ZIP}/bin/avutil-*.dll \
       /tmp/ff/${FFMPEG_ZIP}/bin/swscale-*.dll /tmp/ff/${FFMPEG_ZIP}/bin/swresample-*.dll /out/win-x64/ \
 && cp /tmp/ff/${FFMPEG_ZIP}/LICENSE.txt /out/win-x64/FFMPEG-LICENSE.txt
COPY build/windows/ /out/win-x64/

FROM scratch
COPY --from=linux /out/ /
COPY --from=windows /out/ /
