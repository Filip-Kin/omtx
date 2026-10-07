using libomtnet;

namespace Omtx;

/// <summary>
/// omtx in: omtx sources (phones) -> decoder -> stock OMT senders that vMix and OBS can add.
/// Tally from vMix goes back to the phone. The work is in InBridge / InAllBridge (shared with omtx ui).
/// </summary>
internal static class InCmd
{
    public static int Run(Args a)
    {
        Av.Load(a.Get("--ffmpeg"));
        bool stats = a.Has("--stats");
        string decoderList = a.Get("--decoder");
        var bridges = new List<Bridge>();
        if (a.Positional.Count > 0) foreach (var s in a.Positional) bridges.Add(new InBridge(s, decoderList));
        else bridges.Add(new InAllBridge(decoderList));
        foreach (var b in bridges) b.Start();

        int tick = 0;
        while (Program.Running)
        {
            Thread.Sleep(200);
            if (stats && ++tick % 25 == 0)
                foreach (var b in bridges)
                    foreach (var (src, published, st) in b.Streams())
                        Console.Error.WriteLine($"[in {published}] " + st.Line());
        }
        foreach (var b in bridges) b.Stop();
        return 0;
    }

    // omtx out on the same PC is an omtx source too; bridging it back into vMix makes a loop
    internal static bool IsOwnMachine(OMTAddress s) =>
        string.Equals(s.MachineName, OMTAddress.SanitizeName(Environment.MachineName), StringComparison.OrdinalIgnoreCase);
}
