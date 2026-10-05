using libomtnet;

namespace Omtx;

/// <summary>
/// Bitrate follows backpressure (spec section 4.3): step down 20% on a congestion drop with a
/// 1 s hold, step up 5% after 2 s clean with at most one frame in flight. The ceiling is the
/// configured maximum, capped by the highest quality any receiver suggested (section 4.5).
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
        OMTQuality.Low => 4_000_000,
        OMTQuality.Medium => 8_000_000,
        OMTQuality.High => 15_000_000,
        _ => long.MaxValue,
    };

    /// <summary>Returns true when the target changed.</summary>
    public bool Update(long drops, int inFlight, OMTQuality suggested)
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
                current = Math.Max(floor, (long)(current * 0.8));
                lastDown = now;
            }
        }
        else if (inFlight > 1)
        {
            cleanSince = now;
        }
        else if (now - cleanSince >= TimeSpan.FromSeconds(2))
        {
            current = Math.Min(ceiling, (long)(current * 1.05));
            cleanSince = now;
        }
        if (current > ceiling) current = ceiling;
        return current != before;
    }
}
