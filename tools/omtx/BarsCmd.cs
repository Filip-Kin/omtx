using System.Runtime.InteropServices;
using libomtnet;

namespace Omtx;

/// <summary>
/// omtx bars: colour bars with a moving box and a 1 kHz tone. Stock OMT (VMX) by default,
/// omtx H.264 with --omtx. Paced by the OMT sender clock.
/// </summary>
internal static class BarsCmd
{
    public static int Run(Args a)
    {
        var size = a.Get("--size", "1280x720").Split('x');
        int w = int.Parse(size[0]), h = int.Parse(size[1]);
        int fps = a.Int("--fps", 30);
        bool omtx = a.Has("--omtx");
        string name = a.Get("--name", omtx ? "Bars omtx" : "Bars");

        using var send = omtx ? new OMTSend(name, OMTQuality.Default, OMTAddress.SERVICE_TYPE_OMTX) : new OMTSend(name, OMTQuality.Default);
        Console.Error.WriteLine($"omtx bars: \"{name}\" {w}x{h} {fps} fps {(omtx ? "omtx H.264" : "OMT VMX")}");

        VideoEncoder enc = null;
        AnnexB annexB = null;
        RateControl rc = null;
        if (omtx)
        {
            Av.Load(a.Get("--ffmpeg"));
            rc = new RateControl(3_000_000, a.Int("--bitrate", 10000) * 1000L, a.Int("--bitrate", 10000) * 1000L);
            enc = new VideoEncoder(a.List("--encoder", "libx264"), false, w, h, fps, 1, rc.Current, true, 1.0);
            annexB = new AnnexB(false);
            Console.Error.WriteLine("omtx bars: encoder " + enc.Name);
        }

        int stride = w * 2;
        IntPtr pic = Marshal.AllocHGlobal(stride * h);
        int samples = 48000 / fps;
        IntPtr audio = Marshal.AllocHGlobal(samples * 2 * 4);
        float[] tone = new float[samples * 2];
        double phase = 0;
        long n = 0;
        var vf = new OMTMediaFrame();
        var af = new OMTMediaFrame();
        var outFrame = new OMTMediaFrame();
        try
        {
            while (Program.Running)
            {
                Draw(pic, w, h, stride, n);

                if (send.Connections > 0)
                {
                    // audio first: OMTSend paces each stream on its own clock
                    for (int i = 0; i < samples; i++)
                    {
                        float v = (float)(0.2 * Math.Sin(phase));
                        phase += 2 * Math.PI * 1000 / 48000;
                        tone[i] = v; tone[samples + i] = v; // planar L then R
                    }
                    Marshal.Copy(tone, 0, audio, tone.Length);
                    af.Type = OMTFrameType.Audio; af.Codec = (int)OMTCodec.FPA1; af.Timestamp = -1;
                    af.SampleRate = 48000; af.Channels = 2; af.SamplesPerChannel = samples;
                    af.Data = audio; af.DataLength = tone.Length * 4;
                    send.Send(af);
                }

                if (!omtx)
                {
                    vf.Type = OMTFrameType.Video; vf.Codec = (int)OMTCodec.UYVY; vf.Timestamp = -1;
                    vf.Width = w; vf.Height = h; vf.Stride = stride; vf.FrameRateN = fps; vf.FrameRateD = 1;
                    vf.AspectRatio = (float)w / h; vf.ColorSpace = OMTColorSpace.BT709; vf.Flags = OMTVideoFlags.None;
                    vf.Data = pic; vf.DataLength = stride * h;
                    if (send.Send(vf) == 0) Thread.Sleep(1000 / fps); // nobody connected: the clock does not pace
                }
                else
                {
                    if (send.Connections == 0) { Thread.Sleep(1000 / fps); n++; continue; }
                    if (rc.Update(send.GetCongestionDrops(), send.GetMaxVideoFramesInFlight(), send.ReceiverSuggestedQuality)) enc.SetRate(rc.Current);
                    bool force = send.ConsumeKeyframeRequest();
                    enc.Encode(pic, stride, w, h, Av.PixFmt("uyvy422"), -1, force, (buf, len, key, ts) =>
                    {
                        byte[] au = annexB.Process(buf, len, out bool keyNal);
                        var handle = GCHandle.Alloc(au, GCHandleType.Pinned);
                        try
                        {
                            outFrame.Type = OMTFrameType.Video; outFrame.Codec = (int)OMTCodec.H264; outFrame.Timestamp = -1;
                            outFrame.Width = w; outFrame.Height = h; outFrame.FrameRateN = fps; outFrame.FrameRateD = 1;
                            outFrame.AspectRatio = (float)w / h; outFrame.ColorSpace = OMTColorSpace.BT709;
                            outFrame.Flags = key || keyNal ? OMTVideoFlags.Keyframe : OMTVideoFlags.None;
                            outFrame.Data = handle.AddrOfPinnedObject(); outFrame.DataLength = au.Length;
                            send.Send(outFrame);
                        }
                        finally { handle.Free(); }
                    });
                }
                n++;
            }
        }
        finally
        {
            enc?.Dispose();
            Marshal.FreeHGlobal(pic);
            Marshal.FreeHGlobal(audio);
        }
        return 0;
    }

    // 75% bars (UYVY, BT.709), a white box moving left to right, and a frame counter strip
    static readonly (byte y, byte u, byte v)[] Bars =
    {
        (180, 128, 128), (168, 44, 136), (145, 147, 44), (133, 63, 52),
        (63, 193, 204), (51, 109, 212), (28, 212, 120), (16, 128, 128),
    };

    static unsafe void Draw(IntPtr pic, int w, int h, int stride, long n)
    {
        byte* p = (byte*)pic;
        int box = h / 6;
        int bx = (int)(n * 8 % Math.Max(1, w - box)) & ~1;
        int by = h / 2 - box / 2;
        for (int y = 0; y < h; y++)
        {
            byte* row = p + y * stride;
            for (int x = 0; x < w; x += 2)
            {
                var c = Bars[Math.Min(7, x * 8 / w)];
                if (x >= bx && x < bx + box && y >= by && y < by + box) c = (235, 128, 128);
                if (y >= h - h / 12) c = ((n >> (x * 16 / w)) & 1) == 1 ? ((byte)235, (byte)128, (byte)128) : ((byte)16, (byte)128, (byte)128);
                row[x * 2] = c.u; row[x * 2 + 1] = c.y; row[x * 2 + 2] = c.v; row[x * 2 + 3] = c.y;
            }
        }
    }
}
