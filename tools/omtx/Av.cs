using System.Runtime.InteropServices;
using System.Text;

namespace Omtx;

/// <summary>
/// The few FFmpeg functions omtx needs, resolved at runtime from whichever FFmpeg 4.x to 8.x is
/// installed (Linux) or shipped next to omtx.exe (Windows).
///
/// Codec context fields are only ever set through AVOptions, never through struct offsets,
/// because AVCodecContext changes layout between versions. AVFrame and AVPacket are touched
/// directly, but only their leading fields, which have kept the same layout from libavutil 56
/// (FFmpeg 4) to 59 (FFmpeg 7); libavutil 60 drops AVFrame.key_frame and is handled below.
/// </summary>
internal static unsafe class Av
{
    public const int AV_OPT_SEARCH_CHILDREN = 1;
    public const int AVERROR_EOF = -541478725;
    public const int AV_PKT_FLAG_KEY = 1;
    public const int AV_PICTURE_TYPE_I = 1;
    public static readonly int AVERROR_EAGAIN = OperatingSystem.IsMacOS() ? -35 : -11; // AVERROR(EAGAIN)

    public static bool Loaded { get; private set; }
    public static int UtilMajor { get; private set; }

    // avutil
    static delegate* unmanaged[Cdecl]<uint> p_avutil_version;
    static delegate* unmanaged[Cdecl]<int, void> p_av_log_set_level;
    static delegate* unmanaged[Cdecl]<IntPtr> p_av_frame_alloc;
    static delegate* unmanaged[Cdecl]<IntPtr*, void> p_av_frame_free;
    static delegate* unmanaged[Cdecl]<IntPtr, int, int> p_av_frame_get_buffer;
    static delegate* unmanaged[Cdecl]<IntPtr, int> p_av_frame_make_writable;
    static delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, int, int> p_av_opt_set;
    static delegate* unmanaged[Cdecl]<IntPtr, byte*, long, int, int> p_av_opt_set_int;
    static delegate* unmanaged[Cdecl]<IntPtr, byte*, AVRational, int, int> p_av_opt_set_q;
    static delegate* unmanaged[Cdecl]<byte*, int> p_av_get_pix_fmt;
    static delegate* unmanaged[Cdecl]<int, byte*, nuint, int> p_av_strerror;
    // avcodec
    static delegate* unmanaged[Cdecl]<byte*, IntPtr> p_avcodec_find_encoder_by_name;
    static delegate* unmanaged[Cdecl]<byte*, IntPtr> p_avcodec_find_decoder_by_name;
    static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> p_avcodec_alloc_context3;
    static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> p_avcodec_open2;
    static delegate* unmanaged[Cdecl]<IntPtr*, void> p_avcodec_free_context;
    static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> p_avcodec_send_frame;
    static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> p_avcodec_receive_packet;
    static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> p_avcodec_send_packet;
    static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> p_avcodec_receive_frame;
    static delegate* unmanaged[Cdecl]<IntPtr> p_av_packet_alloc;
    static delegate* unmanaged[Cdecl]<IntPtr*, void> p_av_packet_free;
    static delegate* unmanaged[Cdecl]<IntPtr, void> p_av_packet_unref;
    // swscale
    static delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int, IntPtr, IntPtr, IntPtr, IntPtr> p_sws_getContext;
    static delegate* unmanaged[Cdecl]<IntPtr, byte**, int*, int, int, byte**, int*, int> p_sws_scale;
    static delegate* unmanaged[Cdecl]<IntPtr, void> p_sws_freeContext;

    [StructLayout(LayoutKind.Sequential)]
    public struct AVRational { public int num; public int den; public AVRational(int n, int d) { num = n; den = d; } }

    // avutil major -> matching avcodec / swscale majors
    static readonly (int util, int codec, int sws)[] Versions = { (60, 62, 9), (59, 61, 8), (58, 60, 7), (57, 59, 6), (56, 58, 5) };

    /// <summary>
    /// Load FFmpeg. Searches <paramref name="dir"/> (if given), the folder omtx runs from, then the system paths.
    /// </summary>
    public static void Load(string dir = null)
    {
        if (Loaded) return;
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(dir)) dirs.Add(dir);
        dirs.Add(AppContext.BaseDirectory);
        var errors = new StringBuilder();
        foreach (var v in Versions)
        {
            IntPtr util = Open("avutil", v.util, dirs);
            if (util == IntPtr.Zero) continue;
            IntPtr codec = Open("avcodec", v.codec, dirs);
            IntPtr sws = Open("swscale", v.sws, dirs);
            if (codec == IntPtr.Zero || sws == IntPtr.Zero)
            {
                errors.Append($"avutil {v.util} found without avcodec {v.codec} / swscale {v.sws}. ");
                continue;
            }
            Bind(util, codec, sws);
            UtilMajor = (int)(p_avutil_version() >> 16);
            p_av_log_set_level(16); // AV_LOG_ERROR
            Loaded = true;
            return;
        }
        throw new DllNotFoundException("FFmpeg shared libraries not found (FFmpeg 4 to 8: avutil 56-60, avcodec 58-62, swscale 5-9). " + errors);
    }

    static IntPtr Open(string name, int major, List<string> dirs)
    {
        string file = OperatingSystem.IsWindows() ? $"{name}-{major}.dll"
                    : OperatingSystem.IsMacOS() ? $"lib{name}.{major}.dylib"
                    : $"lib{name}.so.{major}";
        foreach (var d in dirs)
        {
            if (NativeLibrary.TryLoad(Path.Combine(d, file), out IntPtr h)) return h;
        }
        return NativeLibrary.TryLoad(file, out IntPtr hs) ? hs : IntPtr.Zero;
    }

    static void Bind(IntPtr u, IntPtr c, IntPtr s)
    {
        p_avutil_version = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(u, "avutil_version");
        p_av_log_set_level = (delegate* unmanaged[Cdecl]<int, void>)NativeLibrary.GetExport(u, "av_log_set_level");
        p_av_frame_alloc = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(u, "av_frame_alloc");
        p_av_frame_free = (delegate* unmanaged[Cdecl]<IntPtr*, void>)NativeLibrary.GetExport(u, "av_frame_free");
        p_av_frame_get_buffer = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(u, "av_frame_get_buffer");
        p_av_frame_make_writable = (delegate* unmanaged[Cdecl]<IntPtr, int>)NativeLibrary.GetExport(u, "av_frame_make_writable");
        p_av_opt_set = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, int, int>)NativeLibrary.GetExport(u, "av_opt_set");
        p_av_opt_set_int = (delegate* unmanaged[Cdecl]<IntPtr, byte*, long, int, int>)NativeLibrary.GetExport(u, "av_opt_set_int");
        p_av_opt_set_q = (delegate* unmanaged[Cdecl]<IntPtr, byte*, AVRational, int, int>)NativeLibrary.GetExport(u, "av_opt_set_q");
        p_av_get_pix_fmt = (delegate* unmanaged[Cdecl]<byte*, int>)NativeLibrary.GetExport(u, "av_get_pix_fmt");
        p_av_strerror = (delegate* unmanaged[Cdecl]<int, byte*, nuint, int>)NativeLibrary.GetExport(u, "av_strerror");

        p_avcodec_find_encoder_by_name = (delegate* unmanaged[Cdecl]<byte*, IntPtr>)NativeLibrary.GetExport(c, "avcodec_find_encoder_by_name");
        p_avcodec_find_decoder_by_name = (delegate* unmanaged[Cdecl]<byte*, IntPtr>)NativeLibrary.GetExport(c, "avcodec_find_decoder_by_name");
        p_avcodec_alloc_context3 = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)NativeLibrary.GetExport(c, "avcodec_alloc_context3");
        p_avcodec_open2 = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)NativeLibrary.GetExport(c, "avcodec_open2");
        p_avcodec_free_context = (delegate* unmanaged[Cdecl]<IntPtr*, void>)NativeLibrary.GetExport(c, "avcodec_free_context");
        p_avcodec_send_frame = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)NativeLibrary.GetExport(c, "avcodec_send_frame");
        p_avcodec_receive_packet = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)NativeLibrary.GetExport(c, "avcodec_receive_packet");
        p_avcodec_send_packet = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)NativeLibrary.GetExport(c, "avcodec_send_packet");
        p_avcodec_receive_frame = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)NativeLibrary.GetExport(c, "avcodec_receive_frame");
        p_av_packet_alloc = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(c, "av_packet_alloc");
        p_av_packet_free = (delegate* unmanaged[Cdecl]<IntPtr*, void>)NativeLibrary.GetExport(c, "av_packet_free");
        p_av_packet_unref = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(c, "av_packet_unref");

        p_sws_getContext = (delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int, IntPtr, IntPtr, IntPtr, IntPtr>)NativeLibrary.GetExport(s, "sws_getContext");
        p_sws_scale = (delegate* unmanaged[Cdecl]<IntPtr, byte**, int*, int, int, byte**, int*, int>)NativeLibrary.GetExport(s, "sws_scale");
        p_sws_freeContext = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(s, "sws_freeContext");
    }

    // ---- helpers -------------------------------------------------------------------------

    static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    public static string Err(int code)
    {
        byte* buf = stackalloc byte[256];
        p_av_strerror(code, buf, 256);
        return Marshal.PtrToStringUTF8((IntPtr)buf) + $" ({code})";
    }

    public static int PixFmt(string name) { fixed (byte* p = Z(name)) return p_av_get_pix_fmt(p); }

    public static int OptSet(IntPtr obj, string name, string value)
    {
        fixed (byte* n = Z(name)) fixed (byte* v = Z(value)) return p_av_opt_set(obj, n, v, AV_OPT_SEARCH_CHILDREN);
    }
    public static int OptSetInt(IntPtr obj, string name, long value)
    {
        fixed (byte* n = Z(name)) return p_av_opt_set_int(obj, n, value, AV_OPT_SEARCH_CHILDREN);
    }
    public static int OptSetQ(IntPtr obj, string name, int num, int den)
    {
        fixed (byte* n = Z(name)) return p_av_opt_set_q(obj, n, new AVRational(num, den), AV_OPT_SEARCH_CHILDREN);
    }

    public static IntPtr FindEncoder(string name) { fixed (byte* p = Z(name)) return p_avcodec_find_encoder_by_name(p); }
    public static IntPtr FindDecoder(string name) { fixed (byte* p = Z(name)) return p_avcodec_find_decoder_by_name(p); }
    public static IntPtr AllocContext(IntPtr codec) => p_avcodec_alloc_context3(codec);
    public static int Open(IntPtr ctx, IntPtr codec) => p_avcodec_open2(ctx, codec, IntPtr.Zero);
    public static void FreeContext(ref IntPtr ctx) { IntPtr c = ctx; if (c != IntPtr.Zero) p_avcodec_free_context(&c); ctx = IntPtr.Zero; }
    public static int SendFrame(IntPtr ctx, IntPtr frame) => p_avcodec_send_frame(ctx, frame);
    public static int ReceivePacket(IntPtr ctx, IntPtr pkt) => p_avcodec_receive_packet(ctx, pkt);
    public static int SendPacket(IntPtr ctx, IntPtr pkt) => p_avcodec_send_packet(ctx, pkt);
    public static int ReceiveFrame(IntPtr ctx, IntPtr frame) => p_avcodec_receive_frame(ctx, frame);
    public static IntPtr PacketAlloc() => p_av_packet_alloc();
    public static void PacketFree(ref IntPtr pkt) { IntPtr p = pkt; if (p != IntPtr.Zero) p_av_packet_free(&p); pkt = IntPtr.Zero; }
    public static void PacketUnref(IntPtr pkt) => p_av_packet_unref(pkt);
    public static IntPtr FrameAlloc() => p_av_frame_alloc();
    public static void FrameFree(ref IntPtr f) { IntPtr p = f; if (p != IntPtr.Zero) p_av_frame_free(&p); f = IntPtr.Zero; }
    public static int FrameGetBuffer(IntPtr f) => p_av_frame_get_buffer(f, 32);
    public static int FrameMakeWritable(IntPtr f) => p_av_frame_make_writable(f);

    public static IntPtr SwsGet(int sw, int sh, int sf, int dw, int dh, int df) =>
        p_sws_getContext(sw, sh, sf, dw, dh, df, 4 /*SWS_BICUBIC*/, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    public static int SwsScale(IntPtr ctx, byte** src, int* srcStride, int h, byte** dst, int* dstStride) =>
        p_sws_scale(ctx, src, srcStride, 0, h, dst, dstStride);
    public static void SwsFree(IntPtr ctx) { if (ctx != IntPtr.Zero) p_sws_freeContext(ctx); }

    // ---- AVFrame / AVPacket leading fields (64-bit) -------------------------------------
    // AVFrame: data[8] @0, linesize[8] @64, extended_data @96, width @104, height @108,
    // nb_samples @112, format @116, key_frame @120 (removed in avutil 60), pict_type, sample_aspect_ratio, pts @136.
    public static byte** FrameData(IntPtr f) => (byte**)f;
    public static int* FrameLinesize(IntPtr f) => (int*)((byte*)f + 64);
    public static ref int FrameWidth(IntPtr f) => ref *(int*)((byte*)f + 104);
    public static ref int FrameHeight(IntPtr f) => ref *(int*)((byte*)f + 108);
    public static ref int FrameFormat(IntPtr f) => ref *(int*)((byte*)f + 116);
    public static ref int FramePictType(IntPtr f) => ref *(int*)((byte*)f + (UtilMajor >= 60 ? 120 : 124));
    public static ref long FramePts(IntPtr f) => ref *(long*)((byte*)f + 136);

    // AVPacket: buf @0, pts @8, dts @16, data @24, size @32, stream_index @36, flags @40.
    public static ref long PacketPts(IntPtr p) => ref *(long*)((byte*)p + 8);
    public static ref long PacketDts(IntPtr p) => ref *(long*)((byte*)p + 16);
    public static ref IntPtr PacketData(IntPtr p) => ref *(IntPtr*)((byte*)p + 24);
    public static ref int PacketSize(IntPtr p) => ref *(int*)((byte*)p + 32);
    public static ref int PacketFlags(IntPtr p) => ref *(int*)((byte*)p + 40);
}
