# omtx protocol: OMT 1.0 plus inter-frame video

omtx is Open Media Transport 1.0 (`libomtnet/PROTOCOL.md`) with four additions. Everything
not listed here (TCP framing, little-endian headers, subscribe, tally, preview, quality,
OMTInfo, FPA1 audio) is unchanged, and an omtx sender must implement it exactly as stock
OMT does.

## 1. Video codecs

Two FourCC values for the `Codec` field of the 32-byte video extended header:

| Codec | FourCC | INT32 (little-endian) |
|---|---|---|
| H.264 | `H264` | `0x34363248` |
| H.265 | `HEVC` | `0x43564548` |

Payload rules:

- One access unit (one picture) per OMT video frame.
- Annex B byte stream, 4-byte start codes `00 00 00 01`. AUD NAL units are allowed but not needed.
- No B-frames. Decode order equals presentation order.
- Every keyframe carries its parameter sets in band, directly before the IDR slice:
  SPS + PPS for H.264, VPS + SPS + PPS for HEVC. A receiver can start on any keyframe with no
  out-of-band configuration.
- `Width`, `Height`: picture size in pixels (the cropped display size, e.g. 1920x1080).
- `FrameRateN` / `FrameRateD`: nominal frame rate.
- `AspectRatio`: width / height as FLOAT32.
- `ColorSpace`: 709 for HD, 601 for SD.
- `Timestamp` (frame header): capture time in 100 ns units, monotonic.
- No per-frame metadata on video frames (`MetadataLength = 0`), so `DataLength = 32 + payload`.

## 2. Keyframe flag

`OMTVideoFlags.Keyframe = 32` (bit 5) in the video extended header `Flags` field. It is set on
every frame that holds an H.264 IDR (NAL type 5) or an HEVC IRAP picture (NAL types 16 to 21).
Receivers and relays use it to restart decoding without parsing NAL units.

## 3. Keyframe request

A receiver sends this metadata frame, matched as an exact string like the other OMT commands:

```
<OMTKeyframeRequest />
```

The sender makes the next encoded frame a keyframe (H.264 IDR / HEVC IRAP).

## 4. Sender rules

These are what make a long-GOP stream survive Wi-Fi.

1. **A new subscriber starts on a keyframe.** When a connection subscribes to video, the sender
   sends that connection no video until a keyframe, and requests a keyframe from its encoder
   at once.
2. **A dropped frame drops to the next keyframe.** A sender keeps at most 4 video frames in
   flight per connection. If a frame does not fit, the sender drops it and every following
   non-keyframe on that connection, and requests a keyframe. Other connections are not affected.
3. **Bitrate follows backpressure.** Recommended: target bitrate `B` between a floor and a
   ceiling. On a drop, `B = max(floor, 0.8 * B)`, then wait 1 s before any further step down.
   After 2 s with no drop and at most 1 frame in flight, `B = min(ceiling, 1.05 * B)`.
4. **Preview requests are ignored.** `<OMTSettings Preview="true" />` has no meaning for
   H.264/HEVC. The sender keeps sending full frames.
5. **Suggested quality.** `<OMTSettings Quality="..."/>` may cap the ceiling: Low 8 Mbps,
   Medium 15 Mbps; High and Default leave the sender uncapped.

## 5. Discovery

DNS-SD service type **`_omtx._tcp`**, otherwise as OMT: instance name
`HOSTNAME (Source Name)`, port = the TCP listen port (first free port in 6400 to 6600),
no TXT records needed.

The separate type keeps omtx sources out of stock OMT software. vMix, OBS and stock
libomt browse `_omt._tcp` only, so they never see a source they cannot decode. The forked
libomtnet browses both types and registers each source under the type that matches what
it sends.

## 6. Audio

Unchanged: FPA1, 32-bit float planar, 48 kHz recommended, one frame per audio buffer
(e.g. 1024 or 800 samples per channel). Stereo float costs about 3 Mbps.

## 7. Wire example

A 1080p60 H.264 keyframe of 61,440 bytes:

```
01                      Version
02                      FrameType = Video
xx xx xx xx xx xx xx xx Timestamp (INT64, 100 ns)
00 00                   MetadataLength = 0
20 F0 00 00             DataLength = 32 + 61440 = 61472
48 32 36 34             Codec 'H264'
80 07 00 00             Width 1920
38 04 00 00             Height 1080
3C 00 00 00             FrameRateN 60
01 00 00 00             FrameRateD 1
39 8E E3 3F             AspectRatio 1.7777778f
20 00 00 00             Flags = Keyframe
C5 02 00 00             ColorSpace 709
00 00 00 01 67 ...      SPS, PPS, IDR slice (61440 bytes)
```

A metadata frame is the 16-byte header with `FrameType = 1`, `MetadataLength = 0`,
`DataLength = UTF-8 byte length of the XML`, followed by the XML with **no** NUL byte.
Upstream PROTOCOL.md says the XML is NUL-terminated, but libomtnet sends it without one
(`OMTBuffer.FromMetadata`) and matches commands with `==` on the decoded string, so a NUL
would make every command (tally, subscribe, keyframe request) be ignored. Match the code.
