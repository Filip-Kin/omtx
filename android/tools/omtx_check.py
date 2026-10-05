#!/usr/bin/env python3
"""Raw-TCP check of an omtx sender (the camera app or omtx-file-sender.jar).

    python3 omtx_check.py <host> <port> [frames]

Connects like a libomtnet receiver, sends the three OMTSubscribe frames, then checks:
OMTInfo and tally arrive on connect; every frame header is well formed; the first video
frame is a self-contained keyframe (Keyframe flag, SPS+PPS (+VPS) before the IDR); codec,
size and DataLength are consistent; timestamps increase. Also checks tally echo and that a
second connection gets the combined tally. Exit code 0 on success.
"""
import socket
import struct
import sys

HEADER = struct.Struct("<BBqHi")
VIDEO_EXT = struct.Struct("<iiiiifii")
AUDIO_EXT = struct.Struct("<iiiiIi")
FOURCC = {0x34363248: "H264", 0x43564548: "HEVC", 0x31415046: "FPA1"}
KEYFRAME = 32

SUB_VIDEO = '<OMTSubscribe Video="true" />'
SUB_AUDIO = '<OMTSubscribe Audio="true" />'
SUB_META = '<OMTSubscribe Metadata="true" />'
TALLY_PROGRAM = '<OMTTally Preview="false" Program=="true" />'
TALLY_PREVIEW = '<OMTTally Preview="true" Program=="false" />'
TALLY_PREVIEWPROGRAM = '<OMTTally Preview="true" Program=="true" />'
TALLY_NONE = '<OMTTally Preview="false" Program=="false" />'


def meta(xml: str) -> bytes:
    data = xml.encode("utf-8")  # libomtnet sends no trailing NUL
    return HEADER.pack(1, 1, 0, 0, len(data)) + data


def recv_exact(s: socket.socket, n: int) -> bytes:
    buf = bytearray()
    while len(buf) < n:
        chunk = s.recv(n - len(buf))
        if not chunk:
            raise EOFError("connection closed")
        buf += chunk
    return bytes(buf)


def read_frame(s):
    version, ftype, ts, metalen, datalen = HEADER.unpack(recv_exact(s, 16))
    assert version == 1, f"version {version}"
    assert ftype in (1, 2, 4), f"frame type {ftype}"
    assert 0 <= datalen <= 10 * 1024 * 1024, f"data length {datalen}"
    return ftype, ts, metalen, recv_exact(s, datalen)


def nals(payload: bytes, hevc: bool):
    out, i, start = [], 0, None
    while i + 3 <= len(payload):
        if payload[i:i + 3] == b"\x00\x00\x01":
            if start is not None:
                out.append(payload[start:i].rstrip(b"\x00"))
            i += 3
            start = i
        else:
            i += 1
    if start is not None:
        out.append(payload[start:])
    return [((n[0] >> 1) & 0x3F) if hevc else (n[0] & 0x1F) for n in out if n]


def main():
    host, port = sys.argv[1], int(sys.argv[2])
    want = int(sys.argv[3]) if len(sys.argv) > 3 else 120

    s = socket.create_connection((host, port), timeout=10)
    f = read_frame(s)
    assert f[0] == 1 and f[3].decode().startswith("<OMTInfo "), f"expected OMTInfo, got {f}"
    print("info:", f[3].decode())
    f = read_frame(s)
    assert f[0] == 1 and f[3].decode() == TALLY_NONE, f"expected tally none, got {f}"
    print("tally on connect:", f[3].decode())

    for x in (SUB_VIDEO, SUB_AUDIO, SUB_META):
        s.sendall(meta(x))

    video = 0
    keyframes = 0
    last_ts = -1
    first = True
    sizes = []
    while video < want:
        ftype, ts, metalen, data = read_frame(s)
        if ftype == 1:
            print("metadata:", data.decode("utf-8", "replace"))
            continue
        if ftype == 4:
            codec, rate, spc, ch, active, _ = AUDIO_EXT.unpack(data[:24])
            assert FOURCC.get(codec) == "FPA1"
            continue
        codec, w, h, frn, frd, aspect, flags, cs = VIDEO_EXT.unpack(data[:32])
        name = FOURCC.get(codec, hex(codec))
        assert name in ("H264", "HEVC"), f"codec {name}"
        assert metalen == 0, "MetadataLength must be 0"
        payload = data[32:]
        assert payload[:4] == b"\x00\x00\x00\x01", "payload must start with a 4-byte start code"
        hevc = name == "HEVC"
        types = nals(payload, hevc)
        is_key = bool(flags & KEYFRAME)
        key_nal = any(t in range(16, 22) for t in types) if hevc else 5 in types
        assert is_key == key_nal, f"Keyframe flag {is_key} but NAL types {types}"
        if is_key:
            need = {32, 33, 34} if hevc else {7, 8}
            assert need <= set(types), f"keyframe without parameter sets: {types}"
            keyframes += 1
        if first:
            assert is_key, f"first video frame is not a keyframe: NAL types {types}"
            print(f"first video: {name} {w}x{h} {frn}/{frd} aspect {aspect:.7f} flags {flags} colorspace {cs} NALs {types}")
            first = False
        assert ts > last_ts, f"timestamp went backwards {last_ts} -> {ts}"
        last_ts = ts
        sizes.append(len(payload))
        video += 1

    print(f"video frames {video}, keyframes {keyframes}, avg payload {sum(sizes)//len(sizes)} B, last ts {last_ts}")

    # Tally: this connection says program; a second connection must see the combined tally.
    s.sendall(meta(TALLY_PROGRAM))
    s2 = socket.create_connection((host, port), timeout=10)
    read_frame(s2)  # OMTInfo
    got = None
    for _ in range(3):
        ftype, _, _, data = read_frame(s2)
        if ftype == 1 and data.decode().startswith("<OMTTally"):
            got = data.decode()
            break
    print("tally on second connection:", got)
    assert got in (TALLY_PROGRAM, TALLY_NONE), got  # NONE only if our tally had not arrived yet
    s2.sendall(meta(SUB_META) + meta(TALLY_PREVIEW))
    seen = set()
    for _ in range(400):
        ftype, _, _, data = read_frame(s)
        if ftype == 1:
            seen.add(data.decode())
            if TALLY_PREVIEWPROGRAM in seen:
                break
    assert TALLY_PREVIEWPROGRAM in seen, f"combined tally not broadcast: {seen}"
    print("combined tally broadcast:", TALLY_PREVIEWPROGRAM)
    s2.close()
    s.close()
    print("OK")


if __name__ == "__main__":
    main()
