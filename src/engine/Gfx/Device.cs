// The GL device: an EGL context on the app's window (APK) or an off-screen pbuffer (headless
// host on the Thor, for tests). Screenshots for exit tests.
using System;
using System.IO;
using System.IO.Compression;

namespace FF9Engine.Gfx;

public static unsafe class Device {
    public static IntPtr Display, Surface, Context;
    public static int Width = 1920, Height = 1080;
    public static bool Ready;
    public static bool HasWindow => NativeWindow != IntPtr.Zero;
    public static IntPtr NativeWindow;      // set by the APK before Init

    [System.Runtime.InteropServices.DllImport("__Internal")] static extern IntPtr ff9_get_native_window();
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern int ff9_window_gen();
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern void ff9_window_ack(int gen);
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern void ff9_frame_shown();
    static IntPtr config;
    static int windowGen;

    public static void Init() {
        windowGen = ff9_window_gen();
        if (NativeWindow == IntPtr.Zero) NativeWindow = ff9_get_native_window();
        Display = EGL.eglGetDisplay(IntPtr.Zero);
        int maj, min;
        if (EGL.eglInitialize(Display, &maj, &min) == 0) throw new Exception("eglInitialize failed " + EGL.eglGetError());
        EGL.eglBindAPI(EGL.OPENGL_ES_API);
        bool window = NativeWindow != IntPtr.Zero;
        if (window) FrameGen.Init();
        int* attrs = stackalloc int[] {
            EGL.SURFACE_TYPE, window ? EGL.WINDOW_BIT | (FrameGen.On ? EGL.PBUFFER_BIT : 0) : EGL.PBUFFER_BIT, EGL.RENDERABLE_TYPE, EGL.OPENGL_ES3_BIT,
            EGL.RED_SIZE, 8, EGL.GREEN_SIZE, 8, EGL.BLUE_SIZE, 8, EGL.ALPHA_SIZE, 8, EGL.DEPTH_SIZE, 24, EGL.STENCIL_SIZE, 8, EGL.NONE,
        };
        IntPtr cfg;
        int n;
        if (EGL.eglChooseConfig(Display, attrs, &cfg, 1, &n) == 0 || n == 0) throw new Exception("eglChooseConfig failed " + EGL.eglGetError());
        config = cfg;
        int* ctxAttrs = stackalloc int[] { EGL.CONTEXT_CLIENT_VERSION, 3, EGL.NONE };
        Context = EGL.eglCreateContext(Display, cfg, IntPtr.Zero, ctxAttrs);
        if (Context == IntPtr.Zero) throw new Exception("eglCreateContext failed " + EGL.eglGetError());
        if (window) {
            Surface = EGL.eglCreateWindowSurface(Display, cfg, NativeWindow, null);
        } else {
            int* pb = stackalloc int[] { EGL.WIDTH, Width, EGL.HEIGHT, Height, EGL.NONE };
            Surface = EGL.eglCreatePbufferSurface(Display, cfg, pb);
        }
        if (Surface == IntPtr.Zero) throw new Exception("EGL surface failed " + EGL.eglGetError());
        EGL.eglMakeCurrent(Display, Surface, Surface, Context);
        int w, h;
        EGL.eglQuerySurface(Display, Surface, EGL.WIDTH, &w);
        EGL.eglQuerySurface(Display, Surface, EGL.HEIGHT, &h);
        Width = w; Height = h;
        EGL.eglSwapInterval(Display, 1);
        FrameGen.Window(config);
        Ready = true;
        Log.Info($"GL: {GL.GetString(0x1F01)} / {GL.GetString(0x1F02)}, {(window ? "window" : "pbuffer")} {Width}x{Height}");
    }

    static readonly bool finish = Environment.GetEnvironmentVariable("FF9_FINISH") != null;

    public static void Present() {
        if (!Ready) return;
        if (ff9_window_gen() != windowGen) ChangeWindow();
        int shown = FrameGen.Present();
        if (shown < 0) { EGL.eglSwapBuffers(Display, Surface); shown = 1; }
        while (shown-- > 0) ff9_frame_shown();
        if (finish) GL.glFinish();
    }

    /// The app's window went away (sleep, background) or came back: drop the EGL surface, and
    /// while there is no window the game waits here (paused, sound muted); a new window gets a
    /// new surface and the game carries on where it was.
    static void ChangeWindow() {
        bool paused = false;
        while (true) {
            int gen = ff9_window_gen();
            if (gen == windowGen && Surface != IntPtr.Zero) break;
            windowGen = gen;
            if (Surface != IntPtr.Zero) {
                FrameGen.WindowGone();
                EGL.eglMakeCurrent(Display, IntPtr.Zero, IntPtr.Zero, Context);
                EGL.eglDestroySurface(Display, Surface);
                Surface = IntPtr.Zero;
            }
            NativeWindow = ff9_get_native_window();
            if (NativeWindow != IntPtr.Zero) {
                Surface = EGL.eglCreateWindowSurface(Display, config, NativeWindow, null);
                if (Surface != IntPtr.Zero) {
                    EGL.eglMakeCurrent(Display, Surface, Surface, Context);
                    int w, h;
                    EGL.eglQuerySurface(Display, Surface, EGL.WIDTH, &w);
                    EGL.eglQuerySurface(Display, Surface, EGL.HEIGHT, &h);
                    Width = w; Height = h;
                    EGL.eglSwapInterval(Display, 1);
                    FrameGen.Window(config);
                    Log.Info($"window back: {Width}x{Height}");
                } else Log.Warn("EGL surface on the new window failed " + EGL.eglGetError());
            }
            ff9_window_ack(gen);
            if (Surface != IntPtr.Zero) break;
            if (!paused) { paused = true; Log.Info("window gone: game paused"); Core.AppPause.Set(true); }
            System.Threading.Thread.Sleep(50);
        }
        if (paused) { Core.AppPause.Set(false); Log.Info("game resumed"); }
    }

    /// Reads the default framebuffer to a PNG file.
    public static void Screenshot(string path) {
        var px = new byte[Width * Height * 4];
        GL.glBindFramebuffer(GL.FRAMEBUFFER, 0);
        GL.glPixelStorei(GL.PACK_ALIGNMENT, 1);
        fixed (byte* p = px) GL.glReadPixels(0, 0, Width, Height, GL.RGBA, GL.UNSIGNED_BYTE, p);
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;     // what the display shows: no alpha
        Png.Write(path, px, Width, Height, flipY: true);
        Log.Info($"screenshot {path}");
    }
}

/// Minimal PNG encoder/decoder (8-bit RGBA/RGB/grey/palette; no interlacing).
public static class Png {
    static readonly uint[] crcTable = MakeCrc();

    static uint[] MakeCrc() {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
        return t;
    }

    static uint Crc(byte[] b, int off, int len, uint c = 0xFFFFFFFF) {
        for (int i = 0; i < len; i++) c = crcTable[(c ^ b[off + i]) & 0xFF] ^ (c >> 8);
        return c;
    }

    public static void Write(string path, byte[] rgba, int w, int h, bool flipY) {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(fs, "IHDR", ihdr);
        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, true))
            for (int y = 0; y < h; y++) {
                z.WriteByte(0);
                int row = flipY ? h - 1 - y : y;
                z.Write(rgba, row * w * 4, w * 4);
            }
        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data) {
        var hdr = new byte[8];
        BE(hdr, 0, (uint)data.Length);
        for (int i = 0; i < 4; i++) hdr[4 + i] = (byte)type[i];
        s.Write(hdr);
        s.Write(data);
        var all = new byte[4 + data.Length];
        Array.Copy(hdr, 4, all, 0, 4);
        Array.Copy(data, 0, all, 4, data.Length);
        uint c = Crc(all, 0, all.Length) ^ 0xFFFFFFFF;
        var cb = new byte[4];
        BE(cb, 0, c);
        s.Write(cb);
    }

    /// Decodes to RGBA8, rows top to bottom.
    public static byte[]? Read(byte[] f, out int w, out int h) {
        w = h = 0;
        if (f.Length < 8 || f[0] != 0x89 || f[1] != 'P') return null;
        int p = 8, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null, trns = null;
        var idat = new MemoryStream();
        while (p + 8 <= f.Length) {
            int len = (f[p] << 24) | (f[p + 1] << 16) | (f[p + 2] << 8) | f[p + 3];
            string type = System.Text.Encoding.ASCII.GetString(f, p + 4, 4);
            int d = p + 8;
            switch (type) {
                case "IHDR":
                    w = (f[d] << 24) | (f[d + 1] << 16) | (f[d + 2] << 8) | f[d + 3];
                    h = (f[d + 4] << 24) | (f[d + 5] << 16) | (f[d + 6] << 8) | f[d + 7];
                    bitDepth = f[d + 8]; colorType = f[d + 9]; interlace = f[d + 12];
                    break;
                case "PLTE": palette = f[d..(d + len)]; break;
                case "tRNS": trns = f[d..(d + len)]; break;
                case "IDAT": idat.Write(f, d, len); break;
            }
            if (type == "IEND") break;
            p = d + len + 4;
        }
        if (interlace != 0 || bitDepth != 8 && !(colorType == 3 && bitDepth < 8) && bitDepth != 16) return null;
        int ch = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (ch == 0) return null;
        int bpp = Math.Max(1, ch * bitDepth / 8);
        int stride = (w * ch * bitDepth + 7) / 8;
        idat.Position = 0;
        var raw = new byte[(stride + 1) * h];
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) {
            int got = 0;
            while (got < raw.Length) { int r = z.Read(raw, got, raw.Length - got); if (r <= 0) break; got += r; }
        }
        var cur = new byte[stride];
        var prev = new byte[stride];
        var outp = new byte[w * h * 4];
        for (int y = 0; y < h; y++) {
            int ft = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, cur, 0, stride);
            for (int i = 0; i < stride; i++) {
                int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                int v = ft switch {
                    1 => a, 2 => b, 3 => (a + b) >> 1,
                    4 => Paeth(a, b, c), _ => 0,
                };
                cur[i] = (byte)(cur[i] + v);
            }
            for (int x = 0; x < w; x++) {
                int o = (y * w + x) * 4;
                byte r, g, bb, al = 255;
                if (bitDepth == 16) {
                    int s = x * ch * 2;
                    r = cur[s]; g = ch >= 3 ? cur[s + 2] : r; bb = ch >= 3 ? cur[s + 4] : r;
                    al = ch == 4 ? cur[s + 6] : ch == 2 ? cur[s + 2] : (byte)255;
                } else if (colorType == 3) {
                    int idx = bitDepth == 8 ? cur[x] : (cur[x * bitDepth / 8] >> (8 - bitDepth - (x * bitDepth % 8))) & ((1 << bitDepth) - 1);
                    r = palette![idx * 3]; g = palette[idx * 3 + 1]; bb = palette[idx * 3 + 2];
                    al = trns != null && idx < trns.Length ? trns[idx] : (byte)255;
                } else {
                    int s = x * ch;
                    r = cur[s]; g = ch >= 3 ? cur[s + 1] : r; bb = ch >= 3 ? cur[s + 2] : r;
                    al = ch == 4 ? cur[s + 3] : ch == 2 ? cur[s + 1] : (byte)255;
                }
                outp[o] = r; outp[o + 1] = g; outp[o + 2] = bb; outp[o + 3] = al;
            }
            (cur, prev) = (prev, cur);
        }
        return outp;
    }

    static int Paeth(int a, int b, int c) {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
