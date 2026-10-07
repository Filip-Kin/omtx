using System.Runtime.InteropServices;

namespace Omtx;

/// <summary>
/// Minimal SDL2 window for omtx play: one streaming YUV texture, drawn as soon as a picture is
/// decoded. SDL2 is loaded at runtime (it ships with ffplay on every projector), like FFmpeg.
/// </summary>
internal sealed unsafe class SdlWindow : IDisposable
{
    const uint INIT_VIDEO = 0x20, WINDOW_SHOWN = 0x4, WINDOW_BORDERLESS = 0x10, WINDOW_FULLSCREEN_DESKTOP = 0x1001, WINDOW_ALWAYS_ON_TOP = 0x8000;
    const uint RENDERER_ACCELERATED = 0x2, RENDERER_PRESENTVSYNC = 0x4;
    const uint PIXELFORMAT_IYUV = 0x56555949, PIXELFORMAT_UYVY = 0x59565955;
    const int TEXTUREACCESS_STREAMING = 1, WINDOWPOS_UNDEFINED = 0x1FFF0000;
    const uint QUIT = 0x100, KEYDOWN = 0x300;

    static IntPtr lib;
    static delegate* unmanaged[Cdecl]<uint, int> p_Init;
    static delegate* unmanaged[Cdecl]<void> p_Quit;
    static delegate* unmanaged[Cdecl]<byte*, byte*, int> p_SetHint;
    static delegate* unmanaged[Cdecl]<byte*> p_GetError;
    static delegate* unmanaged[Cdecl]<byte*, int, int, int, int, uint, IntPtr> p_CreateWindow;
    static delegate* unmanaged[Cdecl]<IntPtr, void> p_DestroyWindow;
    static delegate* unmanaged[Cdecl]<IntPtr, int, uint, IntPtr> p_CreateRenderer;
    static delegate* unmanaged[Cdecl]<IntPtr, void> p_DestroyRenderer;
    static delegate* unmanaged[Cdecl]<IntPtr, int, int, int> p_RenderSetLogicalSize;
    static delegate* unmanaged[Cdecl]<IntPtr, uint, int, int, int, IntPtr> p_CreateTexture;
    static delegate* unmanaged[Cdecl]<IntPtr, void> p_DestroyTexture;
    static delegate* unmanaged[Cdecl]<IntPtr, void*, byte*, int, byte*, int, byte*, int, int> p_UpdateYUVTexture;
    static delegate* unmanaged[Cdecl]<IntPtr, void*, void*, int, int> p_UpdateTexture;
    static delegate* unmanaged[Cdecl]<IntPtr, int> p_RenderClear;
    static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void*, void*, int> p_RenderCopy;
    static delegate* unmanaged[Cdecl]<IntPtr, void> p_RenderPresent;
    static delegate* unmanaged[Cdecl]<byte*, int> p_PollEvent;
    static delegate* unmanaged[Cdecl]<int, int> p_ShowCursor;
    static delegate* unmanaged[Cdecl]<int, int*, int> p_GetDesktopDisplayMode;

    /// <summary>Loads SDL2; false if it is not installed.</summary>
    public static bool Load()
    {
        if (lib != IntPtr.Zero) return true;
        foreach (var f in OperatingSystem.IsWindows() ? new[] { "SDL2.dll" } : OperatingSystem.IsMacOS() ? new[] { "libSDL2-2.0.0.dylib", "libSDL2.dylib" } : new[] { "libSDL2-2.0.so.0", "libSDL2.so" })
            if (NativeLibrary.TryLoad(f, out lib)) break;
        if (lib == IntPtr.Zero) return false;
        IntPtr G(string n) => NativeLibrary.GetExport(lib, n);
        p_Init = (delegate* unmanaged[Cdecl]<uint, int>)G("SDL_Init");
        p_Quit = (delegate* unmanaged[Cdecl]<void>)G("SDL_Quit");
        p_SetHint = (delegate* unmanaged[Cdecl]<byte*, byte*, int>)G("SDL_SetHint");
        p_GetError = (delegate* unmanaged[Cdecl]<byte*>)G("SDL_GetError");
        p_CreateWindow = (delegate* unmanaged[Cdecl]<byte*, int, int, int, int, uint, IntPtr>)G("SDL_CreateWindow");
        p_DestroyWindow = (delegate* unmanaged[Cdecl]<IntPtr, void>)G("SDL_DestroyWindow");
        p_CreateRenderer = (delegate* unmanaged[Cdecl]<IntPtr, int, uint, IntPtr>)G("SDL_CreateRenderer");
        p_DestroyRenderer = (delegate* unmanaged[Cdecl]<IntPtr, void>)G("SDL_DestroyRenderer");
        p_RenderSetLogicalSize = (delegate* unmanaged[Cdecl]<IntPtr, int, int, int>)G("SDL_RenderSetLogicalSize");
        p_CreateTexture = (delegate* unmanaged[Cdecl]<IntPtr, uint, int, int, int, IntPtr>)G("SDL_CreateTexture");
        p_DestroyTexture = (delegate* unmanaged[Cdecl]<IntPtr, void>)G("SDL_DestroyTexture");
        p_UpdateYUVTexture = (delegate* unmanaged[Cdecl]<IntPtr, void*, byte*, int, byte*, int, byte*, int, int>)G("SDL_UpdateYUVTexture");
        p_UpdateTexture = (delegate* unmanaged[Cdecl]<IntPtr, void*, void*, int, int>)G("SDL_UpdateTexture");
        p_RenderClear = (delegate* unmanaged[Cdecl]<IntPtr, int>)G("SDL_RenderClear");
        p_RenderCopy = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void*, void*, int>)G("SDL_RenderCopy");
        p_RenderPresent = (delegate* unmanaged[Cdecl]<IntPtr, void>)G("SDL_RenderPresent");
        p_PollEvent = (delegate* unmanaged[Cdecl]<byte*, int>)G("SDL_PollEvent");
        p_ShowCursor = (delegate* unmanaged[Cdecl]<int, int>)G("SDL_ShowCursor");
        p_GetDesktopDisplayMode = (delegate* unmanaged[Cdecl]<int, int*, int>)G("SDL_GetDesktopDisplayMode");
        return true;
    }

    static byte[] Z(string s) => System.Text.Encoding.UTF8.GetBytes(s + "\0");
    static string Error() => Marshal.PtrToStringUTF8((IntPtr)p_GetError());
    static void Hint(string k, string v) { fixed (byte* a = Z(k)) fixed (byte* b = Z(v)) p_SetHint(a, b); }

    IntPtr win, ren, tex;
    int texW, texH;
    uint texFmt;

    /// <param name="window">"WxH+X+Y" for a borderless window, null for fullscreen</param>
    public SdlWindow(string title, string window, bool vsync)
    {
        Hint("SDL_RENDER_SCALE_QUALITY", "1");
        Hint("SDL_VIDEO_X11_NET_WM_BYPASS_COMPOSITOR", "1");
        if (p_Init(INIT_VIDEO) != 0) throw new InvalidOperationException("SDL_Init: " + Error());
        int x = WINDOWPOS_UNDEFINED, y = WINDOWPOS_UNDEFINED, w = 1280, h = 720;
        uint flags = WINDOW_SHOWN;
        if (window != null && PlayCmd.TryGeom(window, out int ww, out int wh, out int wx, out int wy))
        {
            w = ww; h = wh; x = wx; y = wy; flags |= WINDOW_BORDERLESS | WINDOW_ALWAYS_ON_TOP;
        }
        else
        {
            // Desktop size up front too: with no window manager (bare X on a projector) the
            // fullscreen flag alone leaves the window at its requested size
            int* mode = stackalloc int[6]; // SDL_DisplayMode: format, w, h, refresh_rate, driverdata
            if (p_GetDesktopDisplayMode(0, mode) == 0 && mode[1] > 0) { w = mode[1]; h = mode[2]; x = y = 0; }
            flags |= WINDOW_FULLSCREEN_DESKTOP;
        }
        fixed (byte* t = Z(title)) win = p_CreateWindow(t, x, y, w, h, flags);
        if (win == IntPtr.Zero) throw new InvalidOperationException("SDL_CreateWindow: " + Error());
        ren = p_CreateRenderer(win, -1, RENDERER_ACCELERATED | (vsync ? RENDERER_PRESENTVSYNC : 0));
        if (ren == IntPtr.Zero) ren = p_CreateRenderer(win, -1, vsync ? RENDERER_PRESENTVSYNC : 0); // software renderer
        if (ren == IntPtr.Zero) throw new InvalidOperationException("SDL_CreateRenderer: " + Error());
        p_ShowCursor(0);
    }

    /// <summary>Copies one I420 picture into the texture (shown on the next Present). Planes are Y, U, V with their pitches.</summary>
    public void Upload(byte* y, int yp, byte* u, int up, byte* v, int vp, int w, int h)
    {
        Texture(PIXELFORMAT_IYUV, w, h);
        p_UpdateYUVTexture(tex, null, y, yp, u, up, v, vp);
    }

    /// <summary>Copies one UYVY picture (stock OMT, decoded from VMX by libomtnet) into the texture.</summary>
    public void UploadUyvy(IntPtr data, int stride, int w, int h)
    {
        Texture(PIXELFORMAT_UYVY, w, h);
        p_UpdateTexture(tex, null, (void*)data, stride);
    }

    void Texture(uint fmt, int w, int h)
    {
        if (tex != IntPtr.Zero && w == texW && h == texH && fmt == texFmt) return;
        if (tex != IntPtr.Zero) p_DestroyTexture(tex);
        tex = p_CreateTexture(ren, fmt, TEXTUREACCESS_STREAMING, w, h);
        if (tex == IntPtr.Zero) throw new InvalidOperationException("SDL_CreateTexture: " + Error());
        p_RenderSetLogicalSize(ren, w, h); // letterbox to the source aspect
        texW = w; texH = h; texFmt = fmt;
    }

    public void Present()
    {
        if (tex == IntPtr.Zero) return;
        p_RenderClear(ren);
        p_RenderCopy(ren, tex, null, null);
        p_RenderPresent(ren);
    }

    /// <summary>Handles window events; false when the window was closed or Esc/q pressed.</summary>
    public bool Pump()
    {
        byte* ev = stackalloc byte[64];
        while (p_PollEvent(ev) != 0)
        {
            uint type = *(uint*)ev;
            if (type == QUIT) return false;
            if (type == KEYDOWN) { int sym = *(int*)(ev + 20); if (sym == 27 || sym == 'q') return false; }
        }
        return true;
    }

    public void Dispose()
    {
        if (tex != IntPtr.Zero) p_DestroyTexture(tex);
        if (ren != IntPtr.Zero) p_DestroyRenderer(ren);
        if (win != IntPtr.Zero) p_DestroyWindow(win);
        tex = ren = win = IntPtr.Zero;
        p_Quit();
    }
}
