# Take N screenshots of the X display, decode the 16-bit ms clock strip (low bits of Unix ms, as
# omtx bars draws it) from each, print latency.
import subprocess, time, sys, statistics
n = int(sys.argv[1]); label = sys.argv[2]
out = []
for i in range(n):
    t0 = time.time()
    raw = subprocess.run(["import", "-window", "root", "-depth", "8", "gray:-"], capture_output=True).stdout
    t1 = time.time()
    W, H = 1280, 720
    y = H - 20
    bits = 0
    for b in range(16):
        x = int((b + 0.5) * W / 16)
        bits = (bits << 1) | (1 if raw[y * W + x] > 128 else 0)
    # the frame was on screen at some moment between t0 and t1
    now0 = int(t0 * 1000) & 0xFFFF
    now1 = int(t1 * 1000) & 0xFFFF
    lat0 = (now0 - bits) & 0xFFFF
    lat1 = (now1 - bits) & 0xFFFF
    out.append((lat0, lat1))
    time.sleep(0.37)
lo = [a for a, b in out]; hi = [b for a, b in out]
print(f"{label}: latency ms, capture start..end: median {statistics.median(lo):.0f}..{statistics.median(hi):.0f}  min {min(lo)}  max {max(hi)}  (n={n})")
