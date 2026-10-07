# Transport options for "NDI HX, but open": research notes

Date: 2026-10-07. Read-only research, no code changed. Numbers are from the cited
source unless marked "memory" or "unverified". Prices, limits and version numbers are
the ones published at the time of writing.

## 0. Targets and what they rule out

| Target | Value |
|---|---|
| Format | 1080p60 |
| Bitrate per stream | under 50 Mbps (venue Wi-Fi can sag to ~50 Mbps total) |
| Glass to glass | under 100 ms, lower is better |
| Discovery | automatic preferred |
| Endpoints | vMix on Windows (RTX 3070 / NVENC on the show PC; AMD RX 7900 XT + Radeon 890M on the test laptop, no NVIDIA), Android phones as cameras (MediaCodec H.264/HEVC), Debian thin clients driving projectors (software decode likely, ffplay/SDL today) |

The bitrate target settles the codec question before any transport question. Every
intra-only codec in the field is over 50 Mbps at 1080p60:

| Codec (1080p60) | Bitrate | Source |
|---|---|---|
| NDI High Bandwidth (SpeedHQ) | ~165 Mbps | ndi.video "NDI for Video" page |
| NDI HX3 H.264 / H.265 | ~62 / ~50 Mbps, GOP 20 | ndi.video, NDI certification technical requirements |
| NDI HX2 H.264 / H.265 | ~62 / ~50 Mbps (ndi.video); third-party guides quote 8 to 40 Mbps | ndi.video, jemproductions.fi |
| OMT VMX Low / Medium / High | 86 / 200 / 260 Mbps | github.com/openmediatransport org README |
| JPEG XS at 10:1 to 20:1 | 125 to 270 Mbps for 4:2:2 10-bit; ~75 to 150 Mbps for 8-bit 4:2:0 (my arithmetic from the 2.7 Gbps and 1.5 Gbps raw rates) | Wikipedia JPEG XS, intoPIX ST 2110-22 blog (6:1 to 15:1 typical) |
| H.264 / HEVC long GOP | 6 to 20 Mbps is normal for 1080p60 camera video; NDI HX3 spends 50 to 62 Mbps for "near full NDI" quality | NDI HX3 figures above; the lower range is common streaming practice (memory) |

So the payload is H.264 or HEVC with inter frames. The real question is only which
transport carries it, which endpoints can speak it natively, and what each one adds in
latency and loss behaviour. That is the frame for everything below.

Reference point for the latency target: NDI HX3 certification demands under 100 ms
glass to glass and an I-frame response under 80 ms (docs.ndi.video technical
requirements). NDI HX2 is allowed 150 ms. Real HX3 cameras are reported at 60 to 80 ms
(third-party guides, unverified). omtx as built measures 30 to 60 ms glass to glass on
loopback with x264 zerolatency (our measurement, from the task brief).

## 1. NDI full bandwidth (SpeedHQ) from vMix

- Bandwidth: ~165 Mbps at 1080p60 (ndi.video). More than three times the budget. Out on
  bandwidth alone. vMix only outputs full-bandwidth NDI, never NDI HX (vMix forum, several
  threads: "vMix will not output NDI-HX. Only full-bandwidth NDI output").
- Latency: NDI's own claim is "sub-frame" / under 100 ms. In vMix, forum users measure NDI
  inputs at about 2 frames behind an SDI input on the same machine (forums.vmix.com
  t17594). That is ~33 ms at 60p of vMix-side input cost, a useful number for any option.
- Licensing: the NDI SDK licence (November 2024 PDF) grants a royalty-free licence to use
  and redistribute the SDK object code only inside a product built with the SDK, requires
  a product release to use an SDK under 30 days old (2.b), forbids distributing SDK files
  except those individually listed (2.d), and forbids reverse engineering of "the SDK ...
  or any protocols used in the SDK" (3.d, 3.i). Only the header files are MIT and may ship
  in an open-source project with dynamic loading (docs.ndi.video "Software Distribution").
  NDI HX encoding is only in the paid NDI Advanced SDK; DistroAV (the OBS NDI plugin) lists
  "Send (encode) HX / HX3" as impossible for a GPL project for that reason (DistroAV
  discussion #1008).
- Open re-implementation: FFmpeg has a SpeedHQ decoder (libavcodec/speedhq.c, reverse
  engineered, merged 2017), and VideoLAN's libndi (LGPL, Jean-Baptiste Kempf,
  code.videolan.org/jbk/libndi, announced September 2025) is a clean-room NDI receiver.
  Its own announcement says sending, NDI HX and the UDP variant are "still to be tackled".
  A sender would need a SpeedHQ encoder (FFmpeg has none) plus the sender side of the
  protocol. Legal position: anyone who has agreed to the NDI SDK licence is contractually
  barred from the reverse engineering; a clean-room project by people who never accepted
  it is in the usual interoperability grey zone, and vMix as the receiver would still be
  running NDI's closed library. Practical position: receive-only today, 165 Mbps anyway.

Verdict: reference point only. Not an option.

## 2. SRT from/to vMix natively

What vMix documents (help29 "SRT" and "SRT" input pages, 29 is the current major,
released 27 October 2025):

| Item | vMix fact | Source |
|---|---|---|
| Codecs out | H.264 or HEVC, AAC-LC audio, MPEG-TS wrapped, up to 8 audio channels | help29/SRT.html |
| Encoder out | "Use accelerated encoding where available on the graphics card"; NVENC presets Low Latency HQ (default), P1 to P7; software x264 presets otherwise | help29/SRT.html, help29/StreamingQuality.html |
| AMD | vMix's streaming encoder is NVENC or Intel QuickSync; AMD AMF is reported as recording-only, with users building a custom FFmpeg to get h264_amf for streaming | forums.vmix.com t33813 "Temporary Solution AMD Streaming Encoder", t25819 |
| NVENC sessions | GeForce: 8 simultaneous encodes in vMix 28 docs; NVIDIA driver 591.44 (December 2025) raised the GeForce cap to 12 | blog.vmix.com "NVIDIA GeForce GPUs now support up to 12 NVENC hardware encodes"; help29/StreamingSettings.html says 8 |
| Per output encoder | Each SRT output is its own encoder instance (the Statistics "Encoder" tab added in vMix 28 counts them); nothing shared | blog.vmix.com vMix 28 |
| How many | 4 SRT outputs in vMix 4K and Pro, 1 in the other editions; the sources are Output 1 to 4 (Outputs 2 to 4 only exist in 4K and up) | help29/SettingsOutputs.html, help28 SRT page |
| Keyframe interval | "maximum distance between keyframes in seconds"; Streaming Quality default is 2 s; integer seconds, so the floor is 1 s | help29/SRT.html, help29/StreamingQuality.html |
| Modes | Caller, Listener, Rendezvous; passphrase; stream ID | help29/SRT.html |
| Latency setting | "at least 4x the ping time to the destination IP. So 20ms would require a latency setting of at least 80" | help29/SRTInput.html |
| Input decode | "Use accelerated decoding where available", H.264 and HEVC | help29/SRTInput.html |
| Input "Decoder Delay" | extra buffer for encoders with uneven frame spacing; "if frames may arrive 100ms apart, set to at least 150" | help29/SRTInput.html |
| Measured | vMix staff: "Glass to glass latency from the vMix SRT output to another vMix SRT input is around 300ms for 1080p29.97 assuming a SRT latency setting of only 30ms"; a demo showed 266 ms / 8 frames; changing codec, hardware vs software encoder or profile made no significant difference | forums.vmix.com t21869 |

SRT itself (libsrt 1.5.7, August 2026 is current):

- Latency parameter: `SRTO_LATENCY` default 120 ms in live mode, minimum 0, and it is the
  minimum receiver buffering delay before a packet is handed up (Haivision
  API-socket-options.md). Haivision's guidance is 3 to 4 times RTT at 1% loss, and their
  table for RTT of 20 ms or less starts at 60 ms (3x) for loss up to 1%, 80 ms (4x) up to
  3%, 120 ms (6x) up to 7% (doc.haivision.com RTT Multiplier). On a wired LAN with RTT
  under 1 ms the arithmetic allows 20 to 40 ms; on Wi-Fi the RTT swings to tens of ms
  during contention, so 60 to 120 ms is the honest setting there. Below that, loss turns
  into TLPKTDROP: both ends drop packets that cannot be delivered in time, the decoder
  sees a hole, and the picture shows artefacts until the next keyframe (Haivision FAQ:
  "visible artifacts and skipped packets at the decoder").
- Bandwidth overhead for retransmission defaults to 25% (`oheadbw`), so a 15 Mbps stream
  is allowed to burst to ~19 Mbps (Haivision FAQ).
- No keyframe request. SRT is a transport; there is no back-channel to ask the encoder for
  an IDR. After a drop the stream heals at the next periodic keyframe, which in vMix is at
  least 1 s away. This is the single biggest functional gap compared with omtx for a
  Wi-Fi camera link.
- Discovery: none. Caller/listener/rendezvous with fixed addresses and ports.
- Android senders: Larix Broadcaster (free, closed source, libsrt 1.5.3, H.264/HEVC via
  MediaCodec, caller/listener/rendezvous) (softvelum.com); open source: ThibaultBee
  srtdroid (JNI binding to libsrt) and StreamPack (Kotlin, Camera2 + MediaCodec, SRT and
  RTMP, version 3.2.1 released 3 October 2026).
- Linux receivers: ffmpeg/ffplay/mpv with libsrt (`srt://host:port?latency=40`),
  GStreamer `srtsrc`, mediamtx. All standard in Debian's FFmpeg build.
- Loss behaviour on Wi-Fi: ARQ recovers anything that can be retransmitted inside the
  latency window; beyond it, TLPKTDROP. Wi-Fi interference loss is treated like
  congestion by SRT's rate estimate, which can throttle unnecessarily (IETF
  draft-sharabayko-mops-srt discussion of wireless loss).

Verdict: fits the bandwidth budget, works on every endpoint with no custom code, but
the vMix end measures ~270 to 300 ms vMix-to-vMix even at a 30 ms latency setting, there
is no discovery, no keyframe-on-demand, one output on non-4K editions, and the AMD
laptop falls back to x264 for the vMix output. The part worth keeping is libsrt as a
media sub-transport (see option 7).

## 3. RTSP/RTP over UDP and MPEG-TS over UDP/multicast

- vMix input: the "Stream / SRT" input type accepts RTSP over UDP or TCP, Transport
  Stream over UDP, TS over TCP, TS over TCP pull, and SRT, decoding H.264 with AAC audio.
  Buffer is 0 to 500 ms for local streams; "Low Latency Mode" is enabled by setting Buffer
  to 0 and ticking the box, and vMix says local devices such as the Teradek Cube then run
  at "a delay of less than 5 frames". If the stream is not low-latency friendly the picture
  shows "compression artefacts and will play back at a lower frame rate" (help29/Stream.html).
  vMix listens on UDP 35000 to 35500 for these inputs. HEVC on the Stream input is not
  documented; the SRT input documents both.
- vMix output: vMix has no UDP/TS or RTSP output type. The Streaming tab is RTMP (and
  built-in providers), outputs are SRT/NDI/OMT (help29/StreamingSettings.html,
  SettingsOutputs.html). A 2015 forum thread (t7226 "Multicast/UDP/FFMPEG") discusses
  pushing a udp:// URL through the custom stream field; unverified for current versions.
- Latency: plain RTP/UDP adds nothing but the receiver jitter buffer; on a LAN ffplay with
  `-fflags nobuffer -flags low_delay` is the same class as omtx's loopback figure. On Wi-Fi
  with no ARQ or FEC every residual loss is a visible hole until the next keyframe.
- FEC: SMPTE 2022-1 / Pro-MPEG CoP3 is in FFmpeg as the `prompeg` protocol, only with the
  `rtp_mpegts` muxer (`-f rtp_mpegts -fec prompeg=l=5:d=20 rtp://...`) (FFmpeg protocols
  docs). vMix does not document 2022-1 FEC on its inputs. FEC costs fixed overhead
  (l x d matrix, typically 5 to 20% extra) and adds l x d packets of delay before a
  correction is possible, which at 15 Mbps and l=5,d=20 is ~100 packets of 1316 bytes,
  about 70 ms. It cannot recover a burst longer than one row/column, and Wi-Fi loss is
  bursty.
- Multicast: fine on a wired switch with IGMP snooping; on Wi-Fi, multicast is sent at the
  lowest basic rate without MAC-layer retries, so it is the worst possible choice for the
  venue AP (memory, standard 802.11 behaviour). Do not plan on it for projectors over Wi-Fi.
- Android: libstreaming (RTSP server, MediaCodec H.264), VideoExpertsGroup
  RTSP.Server.Android, GStreamer on Android. Mostly unmaintained hobby code.

Verdict: TS over UDP into vMix's Stream input with Buffer 0 + Low Latency Mode is the
most interesting piece here, not as a Wi-Fi transport but as the last hop on localhost
from a bridge into vMix without re-encoding (section 10). As a Wi-Fi transport it has no
recovery at all.

## 4. RIST

- libRIST (VideoLAN, BSD-2) implements Simple, Main and Advanced profiles; recovery
  buffer configurable from 50 ms to 30 s; it sets the initial buffer to about 6x RTT when
  min/max are given; RTT from 0 to 5000 ms and up to 1 Gbps (VideoLAN/SipRadius material,
  x-cmd librist page; the primary site was behind an anti-bot wall when fetched). Main
  profile adds encryption and multiplexing; FEC is in the spec but Simple profile is the
  one most products ship (Amazon MediaConnect, Nimble).
- vMix: no RIST input or output. A 2021 feature request thread exists (forums.vmix.com
  t24760) with no implementation since.
- Android: no maintained sender library found. OBS 28+ can send RIST, VLC and FFmpeg
  receive it.
- Latency: same class as SRT (buffer ≥ 50 ms, really 3x to 6x RTT). Same no-keyframe-request
  limitation.

Verdict: technically a sibling of SRT with a cleaner spec, but with no vMix endpoint and
no Android sender it is strictly worse for this project.

## 5. WebRTC (WHIP/WHEP)

- vMix: no WHIP ingest or WHEP output. vMix Call is WebRTC internally but closed and
  Chrome-browser-to-vMix only. Feature request threads (forums.vmix.com t28981 "WHIP
  Support", m112207 "Output webRTC") remain open. WHIP is RFC 9725 (March 2025); WHEP is
  still draft-ietf-wish-whep as of mid 2026.
- Senders: OBS 31+ has native WHIP output (obsproject.com WHIP guide; H.264, HEVC, AV1,
  Opus; B-frames must be off). Browsers via getUserMedia. Android via libwebrtc (large
  dependency) or StreamPack's WHIP extension (unverified).
- Receivers: any browser via WHEP; mediamtx, Broadcast Box, OvenMediaEngine serve WHEP.
  A Chromium kiosk on a Debian thin client works, but H.264 1080p60 decode in Chromium on
  Linux is software unless VA-API is enabled and the GPU is on the allow-list, which on an
  unknown thin-client GPU is a coin toss.
- Latency: libwebrtc's receive jitter buffer is adaptive and conservative; reports put the
  floor at roughly 100 ms even on a clean LAN, and Chrome's playout can sit around 125 ms
  (webrtc jitter-buffer / playoutDelay discussions; the exact figure depends on build and
  content, treat as indicative). `jitterBufferTarget` can be set to 0 with limited effect
  on video. Measured mediamtx WHIP/WHEP deployments report 200 to 400 ms glass to glass
  (adaptnxt benchmark post). Senders also pay the libwebrtc encoder's pacer.
- Loss: NACK + optional FEC (FlexFEC/ULPFEC) + PLI/FIR keyframe requests. This is the one
  open transport that does have keyframe-on-demand built in (RTCP PLI), which is exactly
  the omtx feature, but it is wrapped in a stack whose minimum buffering already spends
  the whole 100 ms budget.
- Bandwidth: fits; browsers cap H.264 bitrates in ways that are hard to control
  (steveseguin/problems-with-webRTC catalogues these).

Verdict: no vMix endpoint, and the jitter buffer alone eats the latency target. Useful
only as a browser monitor path off a hub (section 8), never as the camera-to-vMix link.

## 6. OMT / VMX native

- Protocol: TCP only, mDNS `_omt._tcp` (DNS-SD) or an optional discovery server, MIT
  licence, 32-bit float audio, one TCP connection per receiver (libomtnet PROTOCOL.md,
  github.com/openmediatransport). vMix 29 (27 October 2025) has native OMT input and
  output with Low/Medium/High/Default quality; Default picks the highest quality any
  receiver asks for, and if everyone says Default it is Medium (help29/NDIandOMT1.html).
  OBS via omtplugin; Raspberry Pi 5 omtplayer/omtcapture; Nimble Streamer; several Rust
  ports (omt-rs, vmx-rs).
- VMX codec: intra-only, 4:2:2:4, 8 and 10 bit, AVX2/NEON, "encoding 2160p60 on a single
  Intel i7 core", designed for speed over compression ratio (libvmx README). vmx-rs
  describes it as slice-parallel with a slice-fused encoder, the same structure as libvmx.
- Bandwidth at 1080p60: Low 86 Mbps, Medium 200 Mbps, High 260 Mbps (openmediatransport
  org README; the "~200 Mbps HQ" figure appears in omt-rs too). The lowest preset is 1.7x
  the whole budget. There is no bitrate knob below Low; a vMix forum user asked for a custom
  bitrate option in May 2026 and it does not exist (forums.vmix.com t34197).
- Preview mode: `<OMTSettings Preview="true"/>` makes the sender send a reduced-size frame
  instead of full resolution (PROTOCOL.md). That is a multiviewer feature; it is not a
  low-bitrate program feed.
- Latency: vMix and a hardware vendor both put OMT in the same class as NDI (t34197);
  that is about 2 frames in vMix on top of the encoder's single-frame cost.

### "Skip unchanged slices" (conditional replenishment of VMX slices)

The idea: sender hashes or diffs each slice of the source frame against the last slice it
sent on that connection, sends only changed slices, receiver repaints the rest from its
last frame. Analysis:

- Graphics content (scoreboard, slides, lower thirds on a static bed): most slices are
  bit-identical between frames, so the saving is 80 to 99% on the average frame. But the
  worst case (a full-frame cut, a wipe, a video playing inside the graphic) is still a full
  86 Mbps frame, and Wi-Fi capacity has to be planned for the worst case, not the average.
- Camera content: sensor noise changes every pixel every frame. Exact-match skipping
  saves nothing. A noise threshold turns it into a crude inter codec with no motion
  compensation and no drift control; quality collapses on slow pans (a block that moved by
  one pixel is "unchanged" under a threshold, and errors accumulate until a forced refresh).
- Compatibility: a stock OMT receiver (vMix 29) does not know the frame is partial, so it
  only works omtx-to-omtx, which excludes the vMix input path.
- What it buys over H.264: nothing. An H.264 P-frame of unchanged content is already a
  few kilobytes (skip macroblocks), and x264/NVENC/MediaCodec do that on every platform
  with no custom code. The only argument for VMX over H.264 is decode cost and quality,
  and the budget already rules VMX out.

Verdict: not worth building. VMX's floor is 86 Mbps at 1080p60; conditional
replenishment helps the average on graphics and nothing on cameras, and the worst case is
unchanged. If a graphics-only feed must stay intra, lower the frame rate or resolution
instead.

## 7. Keep omtx (TCP, H.264/HEVC) and improve it

What omtx already has that none of the native options have on the vMix side: mDNS
discovery, keyframe-on-demand, drop-to-IDR, per-connection backpressure bitrate
control, tally. Measured 30 to 60 ms glass to glass on loopback; x264 1080p60 encode ~3
ms on 24 threads.

### Where the latency goes on Wi-Fi

Serialization is the number nobody budgets. At a 50 Mbps effective link, 1 Mbit takes 20
ms on the air. A 1080p60 stream at 15 Mbps averages 31 KB (0.25 Mbit, 5 ms) per frame, but
an IDR at the same quality is commonly 10x the average P-frame (memory, typical x264
behaviour): ~300 KB, 2.4 Mbit, 48 ms of air time, and it arrives behind the queue of
whatever was already in flight. Every keyframe is therefore a latency spike of several
frames on Wi-Fi regardless of transport, and every drop-to-IDR recovery makes one. This
is why NDI HX3 mandates a GOP of 20 with CBR and "low latency" encoder mode: a steady
packet rate, no bursts (docs.ndi.video technical requirements).

### TCP vs UDP+ARQ vs UDP+FEC

| Mechanism | Added delay with no loss | On one lost packet | On a burst | Bandwidth cost |
|---|---|---|---|---|
| TCP (omtx now) | ~0 on LAN | Fast retransmit after 3 dup ACKs (about one RTT, single-digit ms on LAN, 20 to 100 ms on busy Wi-Fi); if it is a tail loss, RTO: Linux floor 200 ms, Windows ~300 ms (memory). Everything behind the hole waits (head-of-line blocking). omtx's 4-frames-in-flight cap then drops to IDR, so the user sees a freeze and a keyframe burst | Same, plus congestion window collapse: TCP reads Wi-Fi loss as congestion and halves cwnd, then ramps slowly | Zero until loss; then a burst |
| UDP + ARQ (SRT, RIST, custom NACK) | Fixed: the latency window (SRT min practical 20 to 40 ms wired, 60 to 120 ms on Wi-Fi) | Retransmitted inside the window, invisible | Packets past the window are dropped (TLPKTDROP), decoder sees a hole, artefact until refresh | oheadbw 25% default |
| UDP + FEC (2022-1, FlexFEC) | l x d packets, tens of ms at these bitrates | Recovered if inside the matrix | Not recoverable if the burst exceeds a row | Fixed 5 to 20% |
| UDP + nothing (RTP) | Jitter buffer only | Hole until refresh | Hole until refresh | 0 |

The 802.11 MAC already retransmits each frame up to 7 times (default retry limit) before
IP sees a loss (IEEE 802.11 default, several papers). So the loss that reaches the
transport is the residue after the MAC gave up, which happens in bursts during
contention or roaming, with latency spikes of tens of ms even when nothing is lost. This
is what makes TCP's two failure modes (HOL blocking and congestion-window collapse) worse
on Wi-Fi than the raw loss rate suggests, and why a bounded-delay UDP window degrades
more gracefully: it turns a freeze into a short artefact.

### Intra-refresh instead of IDR

x264 `--intra-refresh` replaces keyframes with a column of intra macroblocks that sweeps
across the picture; a receiver that lost data is fully clean within one sweep (16 frames
at 16-macroblock spacing in the common configuration), and the per-frame size stays flat,
which removes the keyframe serialization spike above (Doom9 / FFmpeg-user threads, x264
docs). The same exists in NVENC (`enableIntraRefresh`, `intraRefreshPeriod`,
`intraRefreshCnt`; NVIDIA Video Codec SDK programming guide), in Android MediaCodec
(`KEY_INTRA_REFRESH_PERIOD`, API 26+), and in AMD AMF (intra-refresh macroblocks per slot;
memory, verify in the AMF header). Costs: a few percent of bitrate, a new subscriber
still needs a real IDR to start (keep the on-demand IDR for that, it happens once), and
HEVC intra-refresh in MediaCodec is vendor dependent (memory).

With intra-refresh plus a UDP media path, a lost packet becomes a damaged stripe that
heals within ~16 frames (270 ms) with no round trip and no burst. That is the NDI HX3
behaviour pattern.

### Is moving the media to UDP worth it

On a wired LAN: no. TCP at 30 to 60 ms measured is already inside the target and loss is
near zero.

On Wi-Fi: yes, but it is the second step, not the first. Order of work:

1. Intra-refresh (encoder flag on every platform, protocol change is nil: the Keyframe
   flag just appears less often). This removes the keyframe burst, which is the biggest
   single latency hazard on Wi-Fi and is independent of transport. Keep `OMTKeyframeRequest`
   for subscribe and for hard resync.
2. Measure omtx TCP on Wi-Fi with induced loss before changing the transport
   (section 12). If freezes per minute at 1 to 3% loss are acceptable, stop.
3. If not, the cheapest UDP path is libsrt as a per-connection media socket inside omtx:
   keep the TCP connection exactly as it is for subscribe, tally, metadata, keyframe
   request and discovery, and carry video frames over an SRT socket negotiated on it
   (e.g. a metadata command `<OMTXMedia srt="port" latency="40"/>`). libsrt gives the
   ARQ, the latency window, TLPKTDROP and pacing for free, has Android (srtdroid),
   Windows and Linux builds, and ffmpeg can read the same socket. A hand-written NACK
   layer is smaller but has to reinvent the pacing and the window; only do that if libsrt's
   dependency weight is a problem on Android.

Loss behaviour of the result: TLPKTDROP hole, intra-refresh heals it in under a sweep,
receiver can still send `OMTKeyframeRequest` over TCP if it wants an instant clean frame
and is prepared to pay the burst. Stock OMT software still never sees these sources
because of the `_omtx._tcp` service type.

## 8. mediamtx as a hub

mediamtx v1.21.1 (20 September 2026), MIT, single Go binary. Publishes and reads
Media-over-QUIC, SRT, WebRTC (WHIP/WHEP), RTSP, RTMP, HLS, MPEG-TS, RTP; "streams are
automatically converted from a protocol to another" (README). It remuxes, it does not
transcode (memory, consistent with its docs; verify on mediamtx.org before relying on it).
WebRTC reads are limited to codecs browsers accept (H.264 yes; HEVC only in some browsers;
memory).

Where it helps:

- Fan-out to projectors: one SRT or RTSP publish from a bridge, N RTSP/SRT reads by thin
  clients, each in `mpv --profile=low-latency` or `ffplay`. Avoids N encodes and N Wi-Fi
  streams from the source; the hub sits on the wired side.
- Browser monitors via WHEP for anyone with a laptop, no software install.
- Record to disk (fMP4 / TS) as a side effect.
- Path names act as a static directory ("/cam1", "/program"), which is discovery of a
  sort; no mDNS.

Where it does not help: it adds a hop (a few ms, one process) but strips omtx semantics.
No keyframe request passes through SRT or RTSP to the original encoder, tally is gone,
and vMix would read from it via SRT input with the ~300 ms vMix-side cost from section 2,
or via RTSP/TS into the Stream input with Low Latency Mode (undocumented latency, measure).

Verdict: optional sidecar for projector fan-out and browser monitoring, started from the
bridge PC on the wired segment. Not the core transport.

## 9. Other credible open options

- JPEG XS: ISO/IEC 21122, wavelet, latency of 1 to 32 lines (Wikipedia), GStreamer and
  FFmpeg plugins exist (Centricular devlog 2024-09 for GStreamer + MPEG-TS; SVT-JPEG-XS
  ships an FFmpeg plugin, BSD+Patent licence, v0.9.0 June 2024). Patents under RAND
  via the JPEG XS patent portfolio licence (intoPIX, Fraunhofer); "BSD+Patent" covers the
  software, not the codec patents. Bitrate is the killer: typical ST 2110-22 ratios 6:1 to
  15:1, up to 20:1 (intoPIX), i.e. 125 Mbps and up at 1080p60 4:2:2 10-bit. No vMix, no
  Android encoder. Out.
- HTJ2K (OpenJPH, BSD; OpenHTJ2K): same bitrate class as JPEG XS, worse rate control
  (JPEG XS can predict bitrate exactly, HTJ2K cannot without added complexity; VSF
  presentation). Out.
- ST 2110 / IPMX: uncompressed or 2110-22 JPEG XS, PTP, managed switches. Out of budget
  by definition. AES67 is audio only and is already what 2110-30 uses; irrelevant for video.
- Teradek Cube / Kiloview / Magewell (reference only): proprietary H.264/HEVC boxes that
  do SRT, RTSP, NDI HX. Teradek's low-latency RTSP/TS into vMix is what vMix's "less than
  5 frames" Low Latency Mode claim was measured on.
- Media-over-QUIC (MoQ): mediamtx already publishes/reads it; IETF draft; no vMix, no
  Android camera app, browser receive via WebTransport. Watch, do not build on.
- SRT-only hardware encoders on the phone side are not needed; MediaCodec does the job.

## 10. vMix-side integration without the VMX decode/re-encode hop

### Getting already-encoded H.264 out of vMix

| Path | What you get | Cost | Keyframe-on-demand |
|---|---|---|---|
| vMix SRT output, Listener on 127.0.0.1, bridge connects with libsrt and remuxes TS to omtx Annex B | H.264 or HEVC from NVENC (show PC) or x264 (AMD laptop, since vMix does not stream via AMF); AAC audio that would need decoding to FPA1 float or a new omtx audio codec | One of 4 SRT outputs (4K/Pro) or the only one (HD/Basic); vMix staff measure ~270 to 300 ms vMix SRT out to vMix SRT in, split between the two ends unknown, so measure vMix SRT out to ffplay alone | None. Keyframes every N seconds, N ≥ 1. A new omtx subscriber waits up to N s for a clean start unless the bridge caches the last IDR + following P-frames (which then adds up to N s of latency) |
| vMix OMT output to a bridge on the same PC (what omtx does now) | VMX frames, decode cost small (libvmx: 2160p60 on one core), then x264/NVENC/AMF encode | ~1 frame for the VMX hop plus the 3 ms x264; keeps 32-bit float audio as is | Yes, the bridge owns the encoder |
| vMix NDI output to a bridge | SpeedHQ decode via FFmpeg's decoder (reverse engineered) or the NDI SDK | Same as OMT but heavier and licence-encumbered | Yes |
| vMix External output / Desktop capture | Raw frames via DeckLink/virtual device or screen capture | Capture latency, no gain over OMT | Yes |

The OMT hop is the right one for the vMix-to-network direction: VMX decode is cheap, the
bridge keeps control of the encoder, and the alternative (SRT remux) loses the one feature
that makes Wi-Fi tolerable. The AMF upload cost (30 ms/frame via FFmpeg 8.1 system-memory
frames) is a bridge implementation problem (use AMF hardware frames or D3D11 surfaces, or
x264 which is 3 ms anyway), not a reason to change transport.

### Getting H.264 into vMix without re-encoding

| vMix input | Transport | Documented latency | HW decode | Notes |
|---|---|---|---|---|
| SRT input | SRT caller/listener/rendezvous | Latency ≥ 4x ping; Decoder Delay buffer; measured ~300 ms vMix-to-vMix | Yes, H.264 and HEVC | 4 inputs? Not limited like outputs (SRT inputs are ordinary inputs; memory) |
| Stream input, "Transport Stream over UDP" | TS on UDP to 127.0.0.1:35000-35500 | Buffer 0 + Low Latency Mode: "less than 5 frames" with a compatible encoder | Not documented | H.264 + AAC documented; the bridge would remux omtx Annex B into TS and send it to localhost. No tally back to the camera (use the vMix TCP API `TALLY` instead) |
| Stream input, RTSP over UDP/TCP | RTSP | Same Low Latency Mode | Not documented | Bridge would need an RTSP server; TS/UDP is simpler |
| NDI HX input | NDI | Forum: NDI HX "adds several frames" | n/a | Needs an NDI HX sender, which needs the Advanced SDK. Out |
| OMT input (current) | OMT TCP | ~2 frames in vMix (NDI class) | n/a (VMX is CPU) | Keeps tally and quality negotiation |

The Stream input with TS over UDP on localhost is the experiment worth running: it
removes the decode-VMX-encode on the way in, uses vMix's own decoder, and vMix's "less
than 5 frames" claim is 83 ms at 60p, which is comparable to the current OMT hop plus
the bridge's decode. Risks: the Low Latency Mode caveat about artefacts and frame rate if
the stream is not what vMix expects (omtx has no B-frames and in-band SPS/PPS, which is
the friendly case), audio must be AAC in the TS (bridge encodes FPA1 float to AAC, ~1 ms,
or sends audio separately via OMT and video via TS, then vMix lip-sync is on the operator),
and no tally.

What vMix adds on its inputs, from the documentation and forums: NDI/OMT about 2 frames
over SDI; Stream input "less than 5 frames" in Low Latency Mode, up to 500 ms with the
buffer; SRT input the latency parameter plus Decoder Delay plus whatever makes the
vMix-to-vMix figure 270 to 300 ms. None of these are measured by vMix in ms, so the
test plan below measures them.

## 11. Hardware decode on Debian thin clients

| Player | HW decode | Low-latency switches | Notes |
|---|---|---|---|
| ffplay (today) | FFmpeg master/8.x ffplay gained `-hwaccel` (docs: "Use HW accelerated decoding. Enable this option will enable vulkan renderer automatically"). Debian stable's ffplay may predate it; check `ffplay -h` | `-fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0 -framedrop -sync ext` | The hwaccel path routes through the Vulkan renderer, which needs a working Vulkan driver on the thin client; on an unknown GPU that is a risk. Software decode of 1080p60 H.264 is fine on 4 cores, marginal on 2 (memory) |
| mpv | `--hwdec=vaapi` (or `vaapi-copy`), `--vo=gpu` / `--vo=dmabuf-wayland` | `--profile=low-latency` (bundles `--untimed`, `--no-cache`, `--demuxer-lavf-analyzeduration=0`, `--audio-buffer=0`, etc. per mpv manual) | The easiest replacement for ffplay. mpv on Debian reads SRT, RTSP, TS/UDP and files; an omtx-to-TS adapter or an SRT read from mediamtx feeds it |
| GStreamer | `vah264dec` / `vah265dec` (new `va` plugin, GStreamer 1.22+) or legacy `vaapih264dec low-latency=true` | `rtspsrc latency=0`, `udpsrc ! tsdemux ! h264parse ! vah264dec ! waylandsink`/`glimagesink sync=false` | A mailing-list report measured the legacy vaapi decoder adding ~70 ms over software decode because it buffers whole frames before upload; the `va` plugin is newer. Measure, do not assume |
| Chromium (WHEP) | VA-API only with flags and an allow-listed GPU | none | Last resort |

Which codec: H.264 decodes in hardware on anything from Intel Sandy Bridge up and in
software everywhere; HEVC hardware decode starts at Skylake / GCN 3 / Maxwell GM206, and
HEVC software decode at 1080p60 is 2 to 3x the CPU cost of H.264 (memory). For projectors
on unknown thin clients, H.264 is the safe payload; HEVC saves ~30% bitrate on the camera
links where the receiver is the bridge PC with a real GPU. omtx already carries both
FourCCs, so the choice can be per source. Run `vainfo` on each thin client once and record
the result.

## 12. Ranking and what to prototype

| Rank | Option | Bitrate fits | Latency floor | vMix in / out | Android | Thin client | Discovery | Loss recovery | Open |
|---|---|---|---|---|---|---|---|---|---|
| 1 | omtx + intra-refresh, then UDP media via libsrt inside omtx | yes | 30 to 60 ms wired (measured), Wi-Fi bounded by the SRT window | via OMT hop (now) or TS/UDP Stream input (to test) / OMT hop | yes (MediaCodec + srtdroid) | yes (mpv/ffplay via adapter) | mDNS | IDR on demand + intra-refresh + ARQ | MIT |
| 2 | SRT native, vMix SRT in/out | yes | 20 to 40 ms window wired, but vMix end measured ~270 to 300 ms vMix-to-vMix | native, 1 output below 4K/Pro, x264 only on AMD | Larix / StreamPack | yes | none | ARQ only, no IDR request | MPL-2.0 libsrt |
| 3 | mediamtx hub (sidecar to 1 or 2) | yes | +few ms | SRT or Stream input | SRT publish | RTSP/SRT read, WHEP | static paths | passthrough | MIT |
| 4 | RTP/TS over UDP (+2022-1 FEC) | yes | lowest raw floor | Stream input only; no vMix output | hobby libs | yes | none | FEC only, bursts not covered | yes |
| 5 | WebRTC WHIP/WHEP | yes | ≥100 ms jitter buffer on LAN | none | libwebrtc | browser | none | NACK + PLI | yes |
| 6 | RIST | yes | SRT class | none | none | yes | none | ARQ | BSD |
| 7 | OMT native VMX | no (86 Mbps floor) | ~2 frames | native | omt-rs claims Android | Pi 5 player | mDNS | intra | MIT |
| 8 | JPEG XS / HTJ2K | no (≥125 Mbps) | lines | none | none | GStreamer | none | intra | RAND patents |
| 9 | NDI full bandwidth | no (165 Mbps) | <1 frame | native | none | libndi receive only | mDNS | intra | proprietary, anti-RE licence |

Recommendation: keep omtx. It is the only candidate that satisfies bitrate, discovery
and keyframe-on-demand at once, and its measured floor is already inside the target. The
two prototypes worth doing first, in this order:

1. Intra-refresh on every omtx encoder (x264 `--intra-refresh`, NVENC
   `enableIntraRefresh`, MediaCodec `KEY_INTRA_REFRESH_PERIOD`, AMF equivalent), IDR only
   on subscribe and on explicit request. No protocol change. Then the TS-over-UDP localhost
   path into vMix's Stream input with Buffer 0 + Low Latency Mode, measured against the
   current OMT input hop.
2. libsrt as the omtx media socket per connection, negotiated over the existing TCP
   control connection, latency 40 ms wired and 80 to 120 ms on Wi-Fi, TLPKTDROP on. Only
   if the Wi-Fi measurements in the plan below show TCP freezes that intra-refresh alone
   does not fix.

Do not build: VMX slice skipping, an open NDI sender, WebRTC into vMix, RIST, JPEG XS.

### What to measure to decide

Rig: a 60 Hz frame counter (phone stopwatch or an LED strip driven at 60 Hz) in front of
the camera, a photo of source and output side by side, 200 frames per condition for p50
and p99 (the adaptnxt mediamtx benchmark used the same method). Loss injection with
`tc qdisc add dev wlan0 root netem loss 1%` / `3%` / `delay 10ms 20ms` on the Debian
receiver and, for the camera direction, on a Linux AP or the bridge PC's wired port.

| Condition | Metric |
|---|---|
| omtx TCP, wired, 1080p60 15 Mbps | p50/p99 glass to glass (baseline, expect 30 to 60 ms) |
| omtx TCP, 5 GHz Wi-Fi, 0 / 1 / 3% loss | p50/p99, freezes per minute, time from loss to clean picture, bitrate including retransmits |
| omtx TCP + intra-refresh, same | same; keyframe size spike gone? |
| omtx over libsrt (40 / 80 / 120 ms), same | same; artefact seconds per minute instead of freezes |
| vMix SRT out (NVENC, latency 30) to ffplay on wire | p50 glass to glass; isolates the vMix output cost from the 300 ms vMix-to-vMix figure |
| omtx to TS/UDP localhost to vMix Stream input, Low Latency Mode | p50 vs the current OMT-hop input; artefact or frame-rate drop as the help page warns? |
| Thin client: ffplay software vs `ffplay -hwaccel` vs `mpv --hwdec=vaapi --profile=low-latency` vs GStreamer `vah264dec` | decode-to-display delay, CPU %, dropped frames at 1080p60 |
| Android MediaCodec encode (phone model) at 8 / 15 Mbps, H.264 vs HEVC, intra-refresh on | encoder latency (capture timestamp to output buffer), frame size spread |

Decision rule: if omtx + intra-refresh on Wi-Fi at 3% loss stays under 100 ms p99 with
fewer than one freeze per minute, ship it on TCP. If not, move the media to libsrt. In
both cases the vMix input path is whichever of OMT-hop and TS/UDP measures lower without
the warned artefacts.

## Sources

vMix

- https://www.vmix.com/help29/SRT.html (SRT output: codecs, encoder, modes, latency rule, AAC, CBR)
- https://www.vmix.com/help29/SRTInput.html (SRT input: latency 4x ping, Decoder Delay, hardware decode H.264/HEVC)
- https://www.vmix.com/help29/Stream.html (Stream input: RTSP/TS over UDP/TCP, buffer 0 to 500 ms, Low Latency Mode "less than 5 frames", UDP 35000 to 35500)
- https://www.vmix.com/help29/SettingsOutputs.html (Outputs 1 to 4, 4K and higher editions, assignable to SRT/NDI/OMT)
- https://www.vmix.com/help29/NDIandOMT1.html (OMT quality Low/Medium/High/Default, Default rule)
- https://www.vmix.com/help29/StreamingQuality.html (NVENC presets, x264 presets, keyframe default 2 s, HEVC/AV1 need GPU)
- https://www.vmix.com/help29/StreamingSettings.html (RTMP streaming tab, 8 GeForce encodes)
- https://blog.vmix.com/vmix-29-is-available-now/ (vMix 29, 27 Oct 2025, OMT in/out)
- https://blog.vmix.com/vmix-28-is-available-now/ (Encoder statistics tab, 8 encodes)
- https://blog.vmix.com/nvidia-geforce-gpus-now-support-up-to-12-nvenc-hardware-encodes/ (driver 591.44, 12 sessions)
- https://forums.vmix.com/posts/t21869-VMIX-output---encoder-settings-for-the-lowest-SRT-latency (~300 ms vMix-to-vMix at 30 ms SRT latency; 266 ms / 8 frames demo)
- https://forums.vmix.com/posts/t17594-Latency-of-VMIX-NDI-inputs (NDI ~2 frames behind SDI)
- https://forums.vmix.com/posts/t34197-Manufacturer-claims-OMT-image-quality-is-poorer-than-NDI (OMT latency same class as NDI; no custom bitrate)
- https://forums.vmix.com/posts/t33813-Temporary-Solution-AMD-Streaming-Encoder and t25819 (AMD AMF not used for vMix streaming)
- https://forums.vmix.com/posts/t28204-vMix-support-NDI-HX and t27349 (no NDI HX output)
- https://forums.vmix.com/posts/t28981-WHIP-Support, m112207-Output-webRTC (no WHIP/WHEP)
- https://forums.vmix.com/posts/t24760-Support-for-RIST (no RIST)

NDI

- https://ndi.video/tech/ndi-for-video/ (165 / 62 / 50 Mbps at 1080p60; latency claims)
- https://docs.ndi.video/all/developing-with-ndi/ndi-certified/certification-guidelines/technical-requirements (HX3: GOP 20, CBR, <100 ms, I-frame response <80 ms; HX2: <150 ms)
- https://downloads.ndi.tv/SDK/NDI_SDK/NDI%20License%20Agreement.pdf (November 2024; clauses 2.a, 2.b, 2.d, 3.d, 3.h, 3.i quoted above)
- https://docs.ndi.video/all/developing-with-ndi/sdk/software-distribution (MIT headers, dynamic loading)
- https://github.com/DistroAV/DistroAV/discussions/1008 (HX encode needs Advanced SDK, not possible under GPL)
- https://code.videolan.org/jbk/libndi (LGPL, receive only; site was behind an anti-bot page when fetched; status from the VideoLAN announcement and FreshPorts)
- https://ffmpeg.org/doxygen/4.0/speedhq_8c_source.html (SpeedHQ decoder in FFmpeg)

SRT

- https://github.com/Haivision/srt/blob/master/docs/API/API-socket-options.md (SRTO_LATENCY default 120 ms, min 0, TLPKTDROP default on)
- https://doc.haivision.com/SRT/1.5.3/Haivision/rtt-multiplier (RTT multiplier table; 60 / 80 / 120 ms minimums at RTT ≤ 20 ms)
- https://doc.haivision.com/SRT/1.5.3/Haivision/frequently-asked-questions (25% overhead default; artefacts when latency too low; wireless OK)
- https://github.com/Haivision/srt/blob/master/docs/apps/srt-live-transmit.md (3 to 4x RTT guidance)
- https://datatracker.ietf.org/doc/html/draft-sharabayko-mops-srt-01 (TLPKTDROP, Wi-Fi loss vs congestion)
- https://softvelum.com/larix/ (Larix: libsrt 1.5.3, H.264/HEVC, caller/listener/rendezvous)
- https://github.com/ThibaultBee/srtdroid, https://github.com/ThibaultBee/StreamPack (Android SRT; StreamPack 3.2.1, 3 Oct 2026)

OMT

- https://github.com/openmediatransport (org README: 1080p60 High 260 / Medium 200 / Low 86 Mbps; TCP; DNS-SD; MIT)
- https://github.com/openmediatransport/libvmx (intra-only, 4:2:2:4, 10-bit, AVX2, 2160p60 on one core)
- libomtnet/PROTOCOL.md in this repo (Preview and Suggested Quality commands)
- https://github.com/MikanseiLaboratory/vmx-rs (slice-parallel structure)

Others

- https://mediamtx.org and https://github.com/bluenviron/mediamtx (v1.21.1, 20 Sep 2026; protocols; MIT)
- https://obsproject.com/kb/whip-streaming-guide (OBS WHIP, ~100 ms claim, AV1/H.265/Opus)
- https://www.adaptnxt.com/blogs/mediamtx-whip-whep-latency-benchmarks-4-deployments (200 to 400 ms measured WHEP)
- https://lazyharu.com/en/webrtc-playout-delay/ and https://github.com/steveseguin/problems-with-webRTC (jitter buffer floor on LAN)
- https://manpages.debian.org/testing/ffmpeg/ffmpeg-protocols.1.en.html (prompeg FEC with rtp_mpegts)
- https://en.wikipedia.org/wiki/JPEG_XS, https://www.intopix.com/blogs/post/SMPTE-2110-adds-part-22-for-compressed-video, https://github.com/OpenVisualCloud/SVT-JPEG-XS (ratios, latency, BSD+Patent, FFmpeg plugin)
- https://static.vsf.tv/vidtrans/presentations/2020/Smith%20-%20HTJ2K.pdf (HTJ2K vs JPEG XS rate control)
- https://ffmpeg.org/ffplay.html (`-hwaccel` option text)
- https://manpages.ubuntu.com/manpages/jammy/man1/mpv.1.html (`--profile=low-latency`, `--hwdec`)
- https://gstreamer.freedesktop.org/documentation/va/index.html, https://gstreamer.freedesktop.org/documentation/vaapi/vaapih264dec.html (va and vaapi decoders, low-latency property)
- https://docs.nvidia.com/video-technologies/video-codec-sdk/11.0/nvenc-video-encoder-api-prog-guide/index.html (intra refresh fields)
- Android `MediaFormat.KEY_INTRA_REFRESH_PERIOD` (developer.android.com reference)
- IEEE 802.11 retry limit 7 and video-over-WLAN latency: researchgate "Link-layer retransmissions in IEEE 802.11g based industrial networks"; van der Schaar "Adaptive cross-layer protection strategies for robust scalable video transmission over 802.11 WLANs"
