# omtx

Open Media Transport with H.264/HEVC, for live video over Wi-Fi. Stock OMT sends VMX, an
intra-only codec that needs 40+ Mbps for 1080p. omtx keeps OMT's TCP framing, metadata
channel, tally and mDNS discovery, and carries low-latency H.264 at 3 to 15 Mbps instead.

The wire format is in [docs/PROTOCOL-OMTX.md](docs/PROTOCOL-OMTX.md). In short: two new
FourCCs (`H264`, `HEVC`), a keyframe flag, a `<OMTKeyframeRequest />` command, sender rules
for joining and congestion, and a separate DNS-SD type `_omtx._tcp` so vMix/OBS never list a
source they cannot decode.

## Pieces

| Where | What |
|---|---|
| `libomtnet/` | Fork of upstream libomtnet (subtree at `029ef4e`). H.264/HEVC passthrough, drop-to-keyframe, keyframe requests, small send buffers on inter-frame connections, `_omtx._tcp`, discovery that never takes the process down. |
| `tools/omtx/` | One C# binary: `list`, `play`, `out`, `in`, `bars`. FFmpeg 4 to 8 bound at runtime. |
| `android/` | Phone camera sender: Camera2 + MediaCodec + AudioRecord, NsdManager `_omtx._tcp`, tally border. |
| `build/Dockerfile` | Linux (NativeAOT, Debian 11 base) and Windows builds. |
| `dist/` | Built files (not in git). |

## Trying it

**vMix PC (Windows)**: unzip `dist/omtx-windows-x64.zip` anywhere.

- Program out to the projectors: in vMix, Settings > Outputs / NDI / SRT > Output 1 > OMT on.
  Run `omtx-out.cmd`. It finds this PC's vMix OMT output by itself and announces it as
  `<PC> (vMix - Output 1 omtx)` in H.264 (NVENC, falls back to QSV, AMF, then x264).
- Phones into vMix: run `omtx-in.cmd`. Every omtx camera on the network appears in vMix as a
  normal OMT source, Add Input > OMT > `<PC> (<phone> Camera)`. vMix tally goes back to the phone.
- `omtx-list.cmd` shows every OMT and omtx source.
- Windows Firewall asks once for `omtx.exe` (TCP 6400 to 6600, same as vMix's own OMT). Allow on
  private networks.

**Phone**: install `dist/omtx-camera.apk` (sideload), set a source name, Start.

**Any Linux box with ffplay** (Debian 11/12/13): unpack `dist/omtx-linux-x64.tar.gz`, then

```
./omtx list
./omtx play "FIMVIDEO3 (vMix - Output 1 omtx)"        # fullscreen; --window 960x540+0+0 for a window
```

`play` is video only by default; `--audio` adds sound at the cost of latency (ffplay's audio
clock keeps whatever delay builds up). Needs `ffmpeg` (Debian's package includes ffplay) and
`libavahi-client3`.

**Projector display**: branch `omtx-play` in `/home/filip/frc-projector-display-omtx`, on top
of the unreleased `omt-play` branch. omtx sources show up in the phone controller as
`omtx: NAME` with High / Medium / Low. It installs `omtx-play-linux-x86_64.tar.gz` from the
`frc-display-assets` bucket, which is not uploaded yet: it ships with the omt-play release.

## Measured (loopback, Docker, Xvfb + real ffplay)

Latency is from the moment the test pattern draws a frame to that frame on screen, read back
from a clock strip in the picture (`omtx bars`), libx264 software encode:

| Path | 30 fps | 60 fps |
|---|---|---|
| omtx bars -> play | 60-90 ms | 30-60 ms |
| stock VMX bars -> omtx out -> play | 55-85 ms | 28-58 ms |

Link squeezed to 4 Mbit/s under a ~5 Mbps stream: 1 dropped frame, 1 keyframe, bitrate cut to
3.3 Mbps, latency held at 66-97 ms, picture clean. Released: climbs back to the ceiling.

Also checked: FFmpeg 4.3, 7.1 and 8.1 (exact 75% bar colours after two encode/decode
generations), the Android sender's Kotlin wire code against the C# receiver (H.264 and HEVC),
two-container mDNS (play by name, `in` bridging by itself, stock browse sees only the bridged
source), and the Windows build under Wine (full out/in chain with the bundled FFmpeg 8.1 and
libvmx.dll).

## Not yet run on real hardware

- NVENC / NVDEC on the RTX 3070 (only x264 and software decode were exercised).
- Windows DNS-SD for `_omtx._tcp` (Wine lacks `DnsServiceRegister`; the code path is the same
  API vMix's OMT already uses).
- The Android app on a phone: camera session, encoder settings, NSD, `TCP_NOTSENT_LOWAT`.
- Real Wi-Fi.
- vMix's exact OMT source names; `omtx out` with no argument picks any local source containing
  "vMix", Output 1 first, and lists the names when there are several.

## Build

```
DOCKER_BUILDKIT=1 docker build -f build/Dockerfile --output type=local,dest=dist/build .
cd android && ./build.sh
```
