// Frame generation (src/framegen, libff9fg.so): loaded only when the launcher turned it on
// (FF9_FRAMEGEN = <files>/framegen, where the player's Lossless.dll is). Its thread then owns the
// window's surface; the game draws to a pbuffer and hands each frame over.
using System;
using System.Runtime.InteropServices;

namespace FF9Engine.Gfx;

public static unsafe class FrameGen {
    [DllImport("__Internal")] static extern int ff9_movie_recent();
    static delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, int, int> window;
    static delegate* unmanaged<void> windowGone;
    static delegate* unmanaged<int, int, int, int, int> present;
    static bool on;

    /// Before the EGL config is chosen.
    public static void Init() {
        string? dir = Environment.GetEnvironmentVariable("FF9_FRAMEGEN");
        if (string.IsNullOrEmpty(dir)) return;
        try {
            IntPtr lib = NativeLibrary.Load(System.IO.Path.Combine(Environment.GetEnvironmentVariable("FF9_NATIVE") ?? "", "libff9fg.so"));
            var init = (delegate* unmanaged<byte*, int>)NativeLibrary.GetExport(lib, "ff9_fg_init");
            window = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, int, int>)NativeLibrary.GetExport(lib, "ff9_fg_window");
            windowGone = (delegate* unmanaged<void>)NativeLibrary.GetExport(lib, "ff9_fg_window_gone");
            present = (delegate* unmanaged<int, int, int, int, int>)NativeLibrary.GetExport(lib, "ff9_fg_present");
            IntPtr s = Marshal.StringToCoTaskMemUTF8(dir);
            on = init((byte*)s) != 0;
            Marshal.FreeCoTaskMem(s);
        } catch (Exception e) { Log.Warn("frame generation: " + e.Message); }
    }

    public static bool On => on;

    public static void Window(IntPtr config) {
        if (on && Device.HasWindow) window(Device.Display, config, Device.Context, Device.Surface, Device.NativeWindow, Device.Width, Device.Height);
    }

    public static void WindowGone() { if (on) windowGone(); }

    /// Hands the frame over. How many were shown since the last one, -1 = show it as usual.
    public static int Present() => on ? present(Device.Width, Device.Height, ff9_movie_recent(), Core.Clock.TargetFrameRate) : -1;
}
