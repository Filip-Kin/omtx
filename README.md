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
| `android/` | Phone camera sender: Camera2 + MediaCodec + AudioRecord, NsdManager `_omtx._tcp`, tally on screen. |
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

### Camera app

Feature set follows the NDI HX Camera app:

- Rear and front camera. Switching while streaming keeps the stream running when the other
  camera offers the same size and frame rate.
- Pinch zoom on the logical camera through `CONTROL_ZOOM_RATIO` (API 30+), so on a Pixel 9 Pro
  the range runs into the ultrawide (below 1x) and the telephoto lens. The zoom chip shows the
  ratio; tap it for 1x. API 29 falls back to a digital crop.
- Tap to focus and meter at a point (continuous video AF on that region). Focus lock freezes
  focus where it is; a tap while locked focuses once at the new point and holds it.
- Auto exposure, exposure lock, and exposure compensation on a slider (tap the EV value for 0).
- Rule-of-thirds grid, tally as a border, a full-screen tint, or off. Grid, tally mode and
  camera are remembered.
- Receiver count and a dot in the status bar: grey with no receiver, green with one or more.
- With no receiver connected the camera feeds only the preview: the encoder gets no frames and
  does no work. The first connection turns the encoder input on and asks for a keyframe at once,
  so the receiver starts on the next frame.

**Any Linux box** (Debian 11/12/13): unpack `dist/omtx-linux-x64.tar.gz`, then

```
./omtx list
./omtx play "FIMVIDEO3 (vMix - Output 1 omtx)"        # fullscreen; --window 960x540+0+0 for a window
```

`play` is video only by default: it decodes with the system libavcodec and draws each picture
with SDL2 as soon as it is decoded. `--audio` (or `--ffplay`) plays through ffplay instead, at
the cost of latency (ffplay's audio clock keeps whatever delay builds up). Needs `ffmpeg`
(Debian's package brings libavcodec, SDL2 and ffplay) and `libavahi-client3`.

`omtx probe <source>` reads the `omtx bars` clock strip after an in-process decode and prints
per-hop latency, with no display in the loop (`--strip` when bars sits inside a vMix layout,
`--clock http://HOST:6390` to correct for the bars machine's clock through its `omtx ui`).

**Projector display**: branch `omtx-play` in `/home/filip/frc-projector-display-omtx`, on top
of the unreleased `omt-play` branch. omtx sources show up in the phone controller as
`omtx: NAME` with High / Medium / Low. It installs `omtx-play-linux-x86_64.tar.gz` from the
`frc-display-assets` bucket, which is not uploaded yet: it ships with the omt-play release.

## Measured

Latency is from the moment `omtx bars` draws a frame to that frame decoded (`omtx probe`) or on
screen (screenshots of the clock strip), 1080p60, libx264.

Through vMix on the laptop (bars as a vMix input in a PiP, vMix Output 1, receiver on the NAS
over the LAN), p50:

| Hop | Latency |
|---|---|
| bars straight to the NAS (stock VMX) | 21 ms |
| vMix Output 1, stock OMT | 82 ms |
| vMix Output 1 through `omtx out` | 91 ms |
| same, on screen with `omtx play` (SDL2, Xvfb) | 121 ms |

vMix itself adds about 60 ms (input to output) and has p99 spikes near 200 ms; the omtx hop adds
about 9 ms. With ffplay as the player the last row was 155 ms.

Loopback in Docker (Xvfb, `omtx play`): bars -> play at 60 fps, median 20-52 ms (the range is
the screenshot capture window).

Simulated Wi-Fi (40 Mbit/s, 3 ms, a 300 ms near-stall every 5 s, 720p noise at 8 Mbps): 0 frames
dropped, 60 fps held, screen p50 54 ms, p90 74 ms. The tail is the stall itself queued in the
bottleneck buffer. Intra-refresh and BBR made no measurable difference in this simulation.

Also checked: FFmpeg 4.3, 7.1 and 8.1 (exact 75% bar colours after two encode/decode
generations), the Android sender's Kotlin wire code against the C# receiver (H.264 and HEVC),
two-container mDNS (play by name, `in` bridging by itself, stock browse sees only the bridged
source), and the Windows build under Wine (full out/in chain with the bundled FFmpeg 8.1 and
libvmx.dll).

## Not yet run on real hardware

- NVENC / NVDEC on the RTX 3070 (the laptop has AMD only: AMF tops out near 30 fps at 1080p
  through FFmpeg's system-memory upload, so libx264 is ahead of it in the default order).
- Real Wi-Fi to a projector.
- SDL2 on real projector hardware (vsync, GPU renderer); only Xvfb so far.

Run on real hardware: Windows DNS-SD for both service types, the Android app on a Pixel 9 Pro
into vMix, vMix Output 1 through `omtx out`, the `omtx ui` page.

## Build

```
DOCKER_BUILDKIT=1 docker build -f build/Dockerfile --output type=local,dest=dist/build .
cd android && ./build.sh
```
