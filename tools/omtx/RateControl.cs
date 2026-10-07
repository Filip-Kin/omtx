using libomtnet;

namespace Omtx;

/// <summary>
/// Bitrate follows backpressure (spec section 4.3). On a congestion drop the target goes to 80% of
/// what was actually being sent (not of the old target: an encoder below its target would never
/// feel a cut), then holds 1 s. After 1 s clean with at most four frames in flight it rises 25%,
/// so one bad second costs seconds of quality, not minutes.
/// The ceiling is the configured maximum, capped by the highest quality a receiver suggested (4.5).
/// </summary>
internal sealed class RateControl
{
    private readonly long floor, configuredCeiling;
    private long current;
    private long lastDrops;
    private DateTime lastDown = DateTime.MinValue;
    private DateTime cleanSince = DateTime.UtcNow;

    public RateControl(long floor, long ceiling, long start)
    {
        this.floor = floor;
        configuredCeiling = ceiling;
        current = Math.Clamp(start, floor, ceiling);
    }

    public long Current => current;

    public static long CapFor(OMTQuality q) => q switch
    {
        OMTQuality.Low => 8_000_000,
        OMTQuality.Medium => 15_000_000,
        _ => long.MaxValue,
    };

    /// <summary>Returns true when the target changed.</summary>
    public bool Update(long drops, int inFlight, OMTQuality suggested, long measuredBps)
    {
        var now = DateTime.UtcNow;
        long ceiling = Math.Max(floor, Math.Min(configuredCeiling, CapFor(suggested)));
        long before = current;
        long newDrops = drops - lastDrops;
        lastDrops = drops; // can go down when a receiver leaves; only a rise counts

        if (newDrops > 0)
        {
            cleanSince = now;
            if (now - lastDown >= TimeSpan.FromSeconds(1))
            {
                long basis = measuredBps > 0 ? Math.Min(current, measuredBps) : current;
                current = Math.Max(floor, (long)(basis * 0.8));
                lastDown = now;
            }
        }
        else if (inFlight > 4)
        {
            cleanSince = now;
        }
        else if (now - cleanSince >= TimeSpan.FromSeconds(1))
        {
            current = Math.Min(ceiling, (long)(current * 1.25));
            cleanSince = now;
        }
        if (current > ceiling) current = ceiling;
        return current != before;
    }
}

/// <summary>Bits sent over the last second.</summary>
internal sealed class RateMeter
{
    private readonly Queue<(long t, int bytes)> window = new();
    private long total;

    public void Add(int bytes)
    {
        long now = Environment.TickCount64;
        window.Enqueue((now, bytes));
        total += bytes;
        Trim(now);
    }

    public long BitsPerSecond
    {
        get { Trim(Environment.TickCount64); return total * 8; }
    }

    private void Trim(long now)
    {
        while (window.Count > 0 && now - window.Peek().t > 1000) total -= window.Dequeue().bytes;
    }
}
