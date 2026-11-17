// Textures: Texture2D assets (decoded to RGBA8 on the CPU: Adreno has no DXT), runtime
// Texture2D, RenderTexture. Pixel rows are stored bottom-up, as Unity and GL use them.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using FF9Engine.Assets;
using FF9Engine.Core;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FF9Engine.Gfx;

public class NTexture : NObject {
    public int Width, Height, MipCount = 1;
    public TextureFormat Format = TextureFormat.RGBA32;
    public FilterMode Filter = FilterMode.Bilinear;
    public TextureWrapMode Wrap = TextureWrapMode.Repeat;
    public int Aniso = 1;
    public float MipBias;
    public byte[]? Pixels;            // RGBA8, bottom-up; null when only on the GPU
    public bool Dirty = true;         // CPU pixels changed since the last upload
    public uint GlTex;
    public bool Readable = true;
    public bool Linear;
    public bool External;              // a GL texture owned by a native plugin (movies)

    public override void FreeGpu() {
        if (!External) GL.DeleteTexture(GlTex);
        GlTex = 0; Dirty = true;
    }

    public override void ForgetGpu() { GlTex = 0; Dirty = true; External = false; }

    public virtual uint Bind() {
        if (External) return GlTex;
        if (GlTex == 0 || Dirty) Upload();
        return GlTex;
    }

    static readonly string? dumpTex = Environment.GetEnvironmentVariable("FF9_DUMP_TEX");

    public unsafe void Upload() {
        if (!Device.Ready) return;
        if (dumpTex != null && Pixels == null) Log.Once("nopix" + Id, $"texture #{Id} '{Name}' {Width}x{Height} {Format} uploaded without pixels (asset={IsAsset})");
        if (dumpTex != null && Pixels != null && (Name.Contains(dumpTex) || dumpTex == "#" + Id))
            Png.Write($"/data/local/tmp/ff9/shots/tex_{Id}_{Name.Replace('/', '_')}_{Format}.png", Pixels, Width, Height, flipY: true);
        if (GlTex == 0) GlTex = GL.GenTexture();
        Textures.NoteSize(this);
        Core.GpuStats.TexUploads++; Core.GpuStats.LastTex = Name;
        GL.glBindTexture(GL.TEXTURE_2D, GlTex);
        GL.glPixelStorei(GL.UNPACK_ALIGNMENT, 1);
        int max = Textures.MaxSize;
        bool shrunk = false;
        if (Pixels != null && (Width > max || Height > max)) {
            // bigger than this GPU takes (GLES 3 promises only 2048; Mali and PowerVR often stop at
            // 4096 or 8192, Adreno at 16384): a halved copy, same UVs, instead of no texture at all
            var (sp, sw, sh) = Textures.Shrink(Pixels, Width, Height, max);
            Log.Once("bigtex" + Id, $"texture '{Name}' {Width}x{Height} is over the GPU's {max}: uploaded at {sw}x{sh}");
            fixed (byte* p = sp) GL.glTexImage2D(GL.TEXTURE_2D, 0, (int)GL.RGBA8, sw, sh, 0, GL.RGBA, GL.UNSIGNED_BYTE, p);
            shrunk = true;
        }
        else if (Pixels != null) fixed (byte* p = Pixels) GL.glTexImage2D(GL.TEXTURE_2D, 0, (int)GL.RGBA8, Width, Height, 0, GL.RGBA, GL.UNSIGNED_BYTE, p);
        else GL.glTexImage2D(GL.TEXTURE_2D, 0, (int)GL.RGBA8, Math.Max(1, Width), Math.Max(1, Height), 0, GL.RGBA, GL.UNSIGNED_BYTE, null);
        if (MipCount > 1) GL.glGenerateMipmap(GL.TEXTURE_2D);
        ApplySampler();
        Dirty = false;
        if (!Readable && !shrunk) Pixels = null;      // a shrunk copy cannot be read back at full size
    }

    public void ApplySampler() {
        bool mips = MipCount > 1;
        int min = Filter == FilterMode.Point ? (int)(mips ? GL.NEAREST_MIPMAP_NEAREST : GL.NEAREST) : (int)(mips ? GL.LINEAR_MIPMAP_LINEAR : GL.LINEAR);
        GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, min);
        GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, Filter == FilterMode.Point ? (int)GL.NEAREST : (int)GL.LINEAR);
        int wrap = Wrap == TextureWrapMode.Clamp ? (int)GL.CLAMP_TO_EDGE : (int)GL.REPEAT;
        GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, wrap);
        GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, wrap);
    }

    public void EnsurePixels() {
        if (Pixels != null) return;
        Pixels = new byte[Math.Max(1, Width) * Math.Max(1, Height) * 4];
        if (GlTex != 0 && Device.Ready) Textures.ReadBack(this);
    }
}

public static class Textures {
    static NTexture? white, black;

    static int maxSize, largest = 2048;

    /// GL_MAX_TEXTURE_SIZE, or less with FF9_TEST_MAX_TEXTURE (files/env.txt) to try the
    /// shrinking on a GPU that has room (the Thor's Adreno takes 16384).
    public static unsafe int MaxSize {
        get {
            if (maxSize > 0) return maxSize;
            int v = 0;
            GL.glGetIntegerv(GL.MAX_TEXTURE_SIZE, &v);
            if (v < 2048) v = 2048;
            if (int.TryParse(Environment.GetEnvironmentVariable("FF9_TEST_MAX_TEXTURE"), out int t) && t >= 64 && t < v) {
                Log.Info($"GL max texture size {v}, tested as {t} (FF9_TEST_MAX_TEXTURE)");
                v = t;
            } else Log.Info($"GL max texture size {v}");
            return maxSize = v;
        }
    }

    /// Logs each new largest texture over 2048, to learn what the game needs.
    public static void NoteSize(NTexture t) {
        int m = Math.Max(t.Width, t.Height);
        if (m <= largest) return;
        largest = m;
        Log.Info($"largest texture so far: '{t.Name}' {t.Width}x{t.Height}");
    }

    /// Halves (2x2 average, RGBA8) until both sides fit in max.
    public static (byte[] px, int w, int h) Shrink(byte[] px, int w, int h, int max) {
        while (w > max || h > max) {
            int nw = Math.Max(1, (w + 1) / 2), nh = Math.Max(1, (h + 1) / 2);
            var o = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++) {
                int y0 = Math.Min(2 * y, h - 1), y1 = Math.Min(2 * y + 1, h - 1);
                for (int x = 0; x < nw; x++) {
                    int x0 = Math.Min(2 * x, w - 1), x1 = Math.Min(2 * x + 1, w - 1);
                    int a = (y0 * w + x0) * 4, b = (y0 * w + x1) * 4, c = (y1 * w + x0) * 4, d = (y1 * w + x1) * 4, q = (y * nw + x) * 4;
                    for (int k = 0; k < 4; k++) o[q + k] = (byte)((px[a + k] + px[b + k] + px[c + k] + px[d + k] + 2) >> 2);
                }
            }
            px = o; w = nw; h = nh;
        }
        return (px, w, h);
    }

    public static NTexture White => white ??= Solid("UnityWhite", 255, 255, 255, 255);
    public static NTexture Black => black ??= Solid("UnityBlack", 0, 0, 0, 0);
    public static NTexture Grey => grey ??= Solid("UnityGrey", 128, 128, 128, 255);
    public static NTexture Bump => bump ??= Solid("UnityNormalMap", 128, 128, 255, 255);
    static NTexture? grey, bump;

    static NTexture Solid(string name, byte r, byte g, byte b, byte a) {
        var t = new NTexture { Name = name, Width = 4, Height = 4, Pixels = new byte[64], IsAsset = true };
        for (int i = 0; i < 16; i++) { t.Pixels[i * 4] = r; t.Pixels[i * 4 + 1] = g; t.Pixels[i * 4 + 2] = b; t.Pixels[i * 4 + 3] = a; }
        Registry.Add(t, (UObject)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D)));
        return t;
    }

    public static void Register() {
        AssetDB.Readers[ClassIds.Texture2D] = ReadTexture2D;
        AssetDB.Readers[ClassIds.RenderTexture] = ReadRenderTexture;
    }

    /// RenderTexture asset (5.2): name, width, height, antiAliasing, depthFormat, colorFormat,
    /// mipMap, generateMips, sRGB (bools, aligned), then the texture settings (filter, aniso,
    /// mipBias, wrap). The world map's materials point at some (VolcanoCrater1...).
    static void ReadRenderTexture(NAsset a, BinReader r) {
        var t = new NRenderTexture();
        a.Data = t;
        a.Name = t.Name = r.String();
        t.Width = Math.Max(1, r.I32()); t.Height = Math.Max(1, r.I32());
        r.I32();                                  // antiAliasing
        int depth = r.I32();
        t.Depth = depth == 0 ? 0 : depth == 1 ? 16 : 24;
        t.RtFormat = (RenderTextureFormat)r.I32();
        r.Bool(); r.Bool(); r.Bool(); r.Align4();
        t.Filter = (FilterMode)r.I32(); t.Aniso = r.I32(); t.MipBias = r.F32(); t.Wrap = (TextureWrapMode)r.I32();
        t.Readable = false;
    }

    /// Texture2D asset (5.2): name, w, h, size, format, mipCount, readable, readAllowed, imageCount,
    /// dimension, settings (filter, aniso, mipBias, wrap), lightmapFormat, colorSpace, data.
    static void ReadTexture2D(NAsset a, BinReader r) {
        var t = new NTexture();
        a.Data = t;
        a.Name = r.String();
        t.Width = r.I32(); t.Height = r.I32();
        r.I32();
        t.Format = (TextureFormat)r.I32();
        t.MipCount = r.I32();
        t.Readable = r.Bool(); r.Bool(); r.Align4();
        r.I32(); r.I32();
        t.Filter = (FilterMode)r.I32(); t.Aniso = r.I32(); t.MipBias = r.F32(); t.Wrap = (TextureWrapMode)r.I32();
        r.I32(); r.I32();
        int size = r.I32();
        t.Pixels = Decode(r.BytesAt(r.Pos, size), 0, size, t.Width, t.Height, t.Format);
        t.Readable = true;   // keep the CPU copy: the game may read pixels (GetPixels) from assets
        t.Name = a.Name;
    }

    public static NTexture Of(Texture? tex) {
        var n = Registry.Get(tex) ?? throw new NullReferenceException("texture is null");
        return n switch {
            NTexture t => t,
            NAsset { Data: NTexture t } => t,
            _ => throw new InvalidCastException($"{n} is not a texture"),
        };
    }

    public static NTexture? OfOrNull(Texture? tex) {
        var n = Registry.Get(tex);
        return n switch { NTexture t => t, NAsset { Data: NTexture t } => t, _ => null };
    }

    public static Texture2D ManagedWhite => (Texture2D)White.Managed!;

    // ---- pixel formats ----

    public static byte[]? Decode(byte[] d, int off, int size, int w, int h, TextureFormat fmt) {
        var o = new byte[w * h * 4];
        int n = w * h;
        switch (fmt) {
            case TextureFormat.Alpha8:
                for (int i = 0; i < n; i++) { o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = 255; o[i * 4 + 3] = d[off + i]; }
                break;
            case TextureFormat.ARGB4444:
                for (int i = 0; i < n; i++) {
                    int v = d[off + i * 2] | d[off + i * 2 + 1] << 8;
                    o[i * 4 + 3] = (byte)((v >> 12 & 15) * 17); o[i * 4] = (byte)((v >> 8 & 15) * 17);
                    o[i * 4 + 1] = (byte)((v >> 4 & 15) * 17); o[i * 4 + 2] = (byte)((v & 15) * 17);
                }
                break;
            case TextureFormat.RGBA4444:
                for (int i = 0; i < n; i++) {
                    int v = d[off + i * 2] | d[off + i * 2 + 1] << 8;
                    o[i * 4] = (byte)((v >> 12 & 15) * 17); o[i * 4 + 1] = (byte)((v >> 8 & 15) * 17);
                    o[i * 4 + 2] = (byte)((v >> 4 & 15) * 17); o[i * 4 + 3] = (byte)((v & 15) * 17);
                }
                break;
            case TextureFormat.RGB24:
                for (int i = 0; i < n; i++) { o[i * 4] = d[off + i * 3]; o[i * 4 + 1] = d[off + i * 3 + 1]; o[i * 4 + 2] = d[off + i * 3 + 2]; o[i * 4 + 3] = 255; }
                break;
            case TextureFormat.RGBA32:
                Buffer.BlockCopy(d, off, o, 0, n * 4);
                break;
            case TextureFormat.ARGB32:
                for (int i = 0; i < n; i++) { o[i * 4] = d[off + i * 4 + 1]; o[i * 4 + 1] = d[off + i * 4 + 2]; o[i * 4 + 2] = d[off + i * 4 + 3]; o[i * 4 + 3] = d[off + i * 4]; }
                break;
            case TextureFormat.BGRA32:
                for (int i = 0; i < n; i++) { o[i * 4] = d[off + i * 4 + 2]; o[i * 4 + 1] = d[off + i * 4 + 1]; o[i * 4 + 2] = d[off + i * 4]; o[i * 4 + 3] = d[off + i * 4 + 3]; }
                break;
            case TextureFormat.RGB565:
                for (int i = 0; i < n; i++) {
                    int v = d[off + i * 2] | d[off + i * 2 + 1] << 8;
                    o[i * 4] = (byte)((v >> 11) * 255 / 31); o[i * 4 + 1] = (byte)((v >> 5 & 63) * 255 / 63); o[i * 4 + 2] = (byte)((v & 31) * 255 / 31); o[i * 4 + 3] = 255;
                }
                break;
            case TextureFormat.DXT1:
            case TextureFormat.DXT5:
                Dxt.Decode(d, off, w, h, fmt == TextureFormat.DXT5, o);
                break;
            default:
                Log.Once("texfmt" + fmt, $"texture format {fmt} not decoded");
                for (int i = 0; i < n; i++) { o[i * 4] = 255; o[i * 4 + 1] = 0; o[i * 4 + 2] = 255; o[i * 4 + 3] = 255; }
                break;
        }
        return o;
    }

    public static unsafe void ReadBack(NTexture t) {
        uint fbo = GL.GenFramebuffer();
        GL.glBindFramebuffer(GL.FRAMEBUFFER, fbo);
        GL.glFramebufferTexture2D(GL.FRAMEBUFFER, GL.COLOR_ATTACHMENT0, GL.TEXTURE_2D, t.GlTex, 0);
        GL.glPixelStorei(GL.PACK_ALIGNMENT, 1);
        fixed (byte* p = t.Pixels) GL.glReadPixels(0, 0, t.Width, t.Height, GL.RGBA, GL.UNSIGNED_BYTE, p);
        GL.glBindFramebuffer(GL.FRAMEBUFFER, RenderTargets.CurrentFbo);
        GL.DeleteFramebuffer(fbo);
    }

    /// PNG/JPG/DDS/TGA file bytes to RGBA8 bottom-up.
    public static byte[]? DecodeImage(byte[] file, out int w, out int h) {
        byte[]? top = Png.Read(file, out w, out h);
        if (top == null && file.Length > 128 && file[0] == 'D' && file[1] == 'D' && file[2] == 'S') return Dds.Read(file, out w, out h);
        if (top == null) top = Tga.Read(file, out w, out h);
        if (top == null) top = Jpeg.Read(file, out w, out h);
        if (top == null) return null;
        // to bottom-up
        var o = new byte[top.Length];
        int row = w * 4;
        for (int y = 0; y < h; y++) Buffer.BlockCopy(top, y * row, o, (h - 1 - y) * row, row);
        return o;
    }
}

/// S3TC (DXT1/DXT5) block decoding to RGBA8. Unity stores DXT rows bottom-up like other formats.
public static class Dxt {
    public static void Decode(byte[] d, int off, int w, int h, bool dxt5, byte[] o) {
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        Span<byte> col = stackalloc byte[16];
        Span<byte> alpha = stackalloc byte[8];
        int p = off;
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++) {
                ulong abits = 0;
                if (dxt5) {
                    alpha[0] = d[p]; alpha[1] = d[p + 1];
                    if (alpha[0] > alpha[1]) for (int i = 1; i < 7; i++) alpha[i + 1] = (byte)(((7 - i) * alpha[0] + i * alpha[1]) / 7);
                    else { for (int i = 1; i < 5; i++) alpha[i + 1] = (byte)(((5 - i) * alpha[0] + i * alpha[1]) / 5); alpha[6] = 0; alpha[7] = 255; }
                    for (int i = 0; i < 6; i++) abits |= (ulong)d[p + 2 + i] << (8 * i);
                    p += 8;
                }
                int c0 = d[p] | d[p + 1] << 8, c1 = d[p + 2] | d[p + 3] << 8;
                uint bits = (uint)(d[p + 4] | d[p + 5] << 8 | d[p + 6] << 16 | d[p + 7] << 24);
                p += 8;
                Rgb(c0, col, 0); Rgb(c1, col, 4);
                if (c0 > c1 || dxt5) {
                    for (int k = 0; k < 3; k++) { col[8 + k] = (byte)((2 * col[k] + col[4 + k]) / 3); col[12 + k] = (byte)((col[k] + 2 * col[4 + k]) / 3); }
                    col[11] = col[15] = 255;
                } else {
                    for (int k = 0; k < 3; k++) { col[8 + k] = (byte)((col[k] + col[4 + k]) / 2); col[12 + k] = 0; }
                    col[11] = 255; col[15] = 0;
                }
                for (int py = 0; py < 4; py++)
                    for (int px = 0; px < 4; px++) {
                        int x = bx * 4 + px, y = by * 4 + py;
                        int i = py * 4 + px;
                        int ci = (int)(bits >> (2 * i) & 3);
                        if (x >= w || y >= h) continue;
                        int q = (y * w + x) * 4;
                        o[q] = col[ci * 4]; o[q + 1] = col[ci * 4 + 1]; o[q + 2] = col[ci * 4 + 2];
                        o[q + 3] = dxt5 ? alpha[(int)(abits >> (3 * i) & 7)] : col[ci * 4 + 3];
                    }
            }
    }

    /// DXT3: explicit 4-bit alpha per pixel, then a DXT1 colour block (always 4 colours).
    public static void DecodeDxt3(byte[] d, int off, int w, int h, byte[] o) {
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        Span<byte> col = stackalloc byte[16];
        int p = off;
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++) {
                ulong abits = 0;
                for (int i = 0; i < 8; i++) abits |= (ulong)d[p + i] << (8 * i);
                p += 8;
                int c0 = d[p] | d[p + 1] << 8, c1 = d[p + 2] | d[p + 3] << 8;
                uint bits = (uint)(d[p + 4] | d[p + 5] << 8 | d[p + 6] << 16 | d[p + 7] << 24);
                p += 8;
                Rgb(c0, col, 0); Rgb(c1, col, 4);
                for (int k = 0; k < 3; k++) { col[8 + k] = (byte)((2 * col[k] + col[4 + k]) / 3); col[12 + k] = (byte)((col[k] + 2 * col[4 + k]) / 3); }
                for (int py = 0; py < 4; py++)
                    for (int px = 0; px < 4; px++) {
                        int x = bx * 4 + px, y = by * 4 + py;
                        if (x >= w || y >= h) continue;
                        int i = py * 4 + px, ci = (int)(bits >> (2 * i) & 3), q = (y * w + x) * 4;
                        o[q] = col[ci * 4]; o[q + 1] = col[ci * 4 + 1]; o[q + 2] = col[ci * 4 + 2];
                        o[q + 3] = (byte)((abits >> (4 * i) & 15) * 17);
                    }
            }
    }

    static void Rgb(int c, Span<byte> o, int at) {
        o[at] = (byte)((c >> 11 & 31) * 255 / 31); o[at + 1] = (byte)((c >> 5 & 63) * 255 / 63); o[at + 2] = (byte)((c & 31) * 255 / 31); o[at + 3] = 255;
    }
}

/// DDS (DXT1/DXT3/DXT5/uncompressed 32-bit), as used by mods. Returns rows top-down.
public static class Dds {
    public static byte[]? Read(byte[] f, out int w, out int h) {
        h = BitConverter.ToInt32(f, 12); w = BitConverter.ToInt32(f, 16);
        int pfFlags = BitConverter.ToInt32(f, 80);
        string four = System.Text.Encoding.ASCII.GetString(f, 84, 4);
        int bits = BitConverter.ToInt32(f, 88);
        int off = 128;
        var o = new byte[w * h * 4];
        if ((pfFlags & 4) != 0 && (four == "DXT1" || four == "DXT5")) {
            Dxt.Decode(f, off, w, h, four == "DXT5", o);
            return o;
        }
        if ((pfFlags & 4) != 0 && four == "DXT3") {
            Dxt.DecodeDxt3(f, off, w, h, o);
            return o;
        }
        if ((pfFlags & 4) != 0 && four == "DX10") {
            // DX10 header (20 bytes more): BC1/BC2/BC3 by DXGI format; BC7 (98/99) is not decoded yet
            int dxgi = BitConverter.ToInt32(f, 128);
            off = 148;
            if (dxgi is 71 or 72) { Dxt.Decode(f, off, w, h, false, o); return o; }
            if (dxgi is 74 or 75) { Dxt.DecodeDxt3(f, off, w, h, o); return o; }
            if (dxgi is 77 or 78) { Dxt.Decode(f, off, w, h, true, o); return o; }
            Log.Once("dds-dxgi" + dxgi, $"DDS DXGI format {dxgi} not supported (98/99: BC7)");
            return null;
        }
        if ((pfFlags & 0x40) != 0 && bits == 32) {
            uint rm = BitConverter.ToUInt32(f, 92), gm = BitConverter.ToUInt32(f, 96), bm = BitConverter.ToUInt32(f, 100), am = BitConverter.ToUInt32(f, 104);
            for (int i = 0; i < w * h; i++) {
                uint v = BitConverter.ToUInt32(f, off + i * 4);
                o[i * 4] = Ch(v, rm); o[i * 4 + 1] = Ch(v, gm); o[i * 4 + 2] = Ch(v, bm); o[i * 4 + 3] = am == 0 ? (byte)255 : Ch(v, am);
            }
            return o;
        }
        Log.Once("dds" + four, $"DDS format {four}/{bits} not supported");
        return null;
    }

    static byte Ch(uint v, uint mask) {
        if (mask == 0) return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        return (byte)((v & mask) >> shift);
    }
}

/// Uncompressed / RLE TGA (24/32-bit). Returns rows top-down.
public static class Tga {
    public static byte[]? Read(byte[] f, out int w, out int h) {
        w = h = 0;
        if (f.Length < 18) return null;
        int idLen = f[0], cmap = f[1], type = f[2];
        if (cmap != 0 || type != 2 && type != 10) return null;
        w = f[12] | f[13] << 8; h = f[14] | f[15] << 8;
        int bpp = f[16] / 8;
        bool topDown = (f[17] & 0x20) != 0;
        if (bpp != 3 && bpp != 4 || w <= 0 || h <= 0) return null;
        int W = w, H = h;
        var o = new byte[w * h * 4];
        int p = 18 + idLen, n = 0;
        void Put(int i, int src) {
            int y = i / W, x = i % W;
            if (!topDown) y = H - 1 - y;
            int q = (y * W + x) * 4;
            o[q] = f[src + 2]; o[q + 1] = f[src + 1]; o[q + 2] = f[src]; o[q + 3] = bpp == 4 ? f[src + 3] : (byte)255;
        }
        while (n < w * h && p < f.Length) {
            if (type == 2) { Put(n++, p); p += bpp; continue; }
            int c = f[p++];
            int cnt = (c & 127) + 1;
            if ((c & 128) != 0) { for (int i = 0; i < cnt && n < w * h; i++) Put(n++, p); p += bpp; }
            else for (int i = 0; i < cnt && n < w * h; i++) { Put(n++, p); p += bpp; }
        }
        return o;
    }
}

public static class Jpeg {
    public static byte[]? Read(byte[] f, out int w, out int h) {
        w = h = 0;
        if (f.Length > 2 && f[0] == 0xFF && f[1] == 0xD8) Log.Once("jpeg", "JPEG images are not decoded yet");
        return null;
    }
}

public sealed class NRenderTexture : NTexture {
    public int Depth = 24;
    public RenderTextureFormat RtFormat = RenderTextureFormat.ARGB32;
    public uint Fbo, DepthRb;
    public bool Created;
    public bool Temporary, TempInUse;

    public bool Create() {
        if (Created) return true;
        if (!Device.Ready) return false;
        GlTex = GL.GenTexture();
        Core.GpuStats.RtCreates++;
        Pixels = null;
        Readable = false;
        Upload();
        Fbo = GL.GenFramebuffer();
        GL.glBindFramebuffer(GL.FRAMEBUFFER, Fbo);
        GL.glFramebufferTexture2D(GL.FRAMEBUFFER, GL.COLOR_ATTACHMENT0, GL.TEXTURE_2D, GlTex, 0);
        if (Depth > 0) {
            DepthRb = GL.GenRenderbuffer();
            GL.glBindRenderbuffer(GL.RENDERBUFFER, DepthRb);
            GL.glRenderbufferStorage(GL.RENDERBUFFER, GL.DEPTH24_STENCIL8, Width, Height);
            GL.glFramebufferRenderbuffer(GL.FRAMEBUFFER, GL.DEPTH_STENCIL_ATTACHMENT, GL.RENDERBUFFER, DepthRb);
        }
        uint st = GL.glCheckFramebufferStatus(GL.FRAMEBUFFER);
        if (st != GL.FRAMEBUFFER_COMPLETE) Log.Warn($"render texture {Width}x{Height}: framebuffer status {st:x}");
        GL.glBindFramebuffer(GL.FRAMEBUFFER, RenderTargets.CurrentFbo);
        Created = true;
        Dirty = false;
        return true;
    }

    public override uint Bind() {
        if (!Created) Create();
        return GlTex;
    }

    public override void FreeGpu() => Release();
    public override void ForgetGpu() { GlTex = Fbo = DepthRb = 0; Created = false; }

    public void Release() {
        if (!Created) return;
        GL.DeleteTexture(GlTex); GL.DeleteFramebuffer(Fbo); GL.DeleteRenderbuffer(DepthRb);
        GlTex = Fbo = DepthRb = 0;
        Created = false;
    }
}

public static class RenderTargets {
    public static NRenderTexture? Active;
    public static uint CurrentFbo => Active?.Fbo ?? 0;

    public static void Set(NRenderTexture? rt) {
        Active = rt;
        if (!Device.Ready) return;
        if (rt != null) rt.Create();
        GL.glBindFramebuffer(GL.FRAMEBUFFER, CurrentFbo);
        int w = rt?.Width ?? Device.Width, h = rt?.Height ?? Device.Height;
        GL.glViewport(0, 0, w, h);
    }

    public static int Width => Active?.Width ?? Device.Width;
    public static int Height => Active?.Height ?? Device.Height;
}

static class TextureIcalls {
    static NTexture T(Texture t) => Textures.Of(t);

    [Icall("UnityEngine.Texture::Internal_GetWidth")] static int W(Texture t) => T(t).Width;
    [Icall("UnityEngine.Texture::Internal_GetHeight")] static int H(Texture t) => T(t).Height;
    [Icall("UnityEngine.Texture::get_filterMode")] static FilterMode GF(Texture t) => T(t).Filter;
    [Icall("UnityEngine.Texture::set_filterMode")]
    static void SF(Texture t, FilterMode v) {
        var x = T(t); x.Filter = v;
        if (x is NRenderTexture { Created: true } || x.External) { if (Device.Ready && x.GlTex != 0) { GL.glBindTexture(GL.TEXTURE_2D, x.GlTex); x.ApplySampler(); } }
        else x.Dirty |= x.GlTex != 0;
    }
    [Icall("UnityEngine.Texture::get_anisoLevel")] static int GA(Texture t) => T(t).Aniso;
    [Icall("UnityEngine.Texture::set_anisoLevel")] static void SA(Texture t, int v) => T(t).Aniso = v;
    [Icall("UnityEngine.Texture::get_wrapMode")] static TextureWrapMode GW(Texture t) => T(t).Wrap;
    [Icall("UnityEngine.Texture::set_wrapMode")]
    static void SW(Texture t, TextureWrapMode v) {
        var x = T(t); x.Wrap = v;
        if (x is NRenderTexture { Created: true } || x.External) { if (Device.Ready && x.GlTex != 0) { GL.glBindTexture(GL.TEXTURE_2D, x.GlTex); x.ApplySampler(); } }
        else x.Dirty |= x.GlTex != 0;
    }
    [Icall("UnityEngine.Texture::get_mipMapBias")] static float GMB(Texture t) => T(t).MipBias;
    [Icall("UnityEngine.Texture::set_mipMapBias")] static void SMB(Texture t, float v) => T(t).MipBias = v;
    [Icall("UnityEngine.Texture::INTERNAL_get_texelSize")] static void Texel(Texture t, ref Vector2 v) { var x = T(t); v = new Vector2(1f / x.Width, 1f / x.Height); }
    [Icall("UnityEngine.Texture::GetNativeTexturePtr")] static IntPtr NativePtr(Texture t) => (IntPtr)T(t).Bind();
    [Icall("UnityEngine.Texture::GetNativeTextureID")] static int NativeId(Texture t) => (int)T(t).Bind();
    [Icall("UnityEngine.Texture::get_masterTextureLimit")] static int Limit() => 0;
    [Icall("UnityEngine.Texture::set_masterTextureLimit")] static void SetLimit(int v) { }
    [Icall("UnityEngine.Texture::set_anisotropicFiltering")] static void SetAnisoMode(AnisotropicFiltering v) { }

    [Icall("UnityEngine.Texture2D::Internal_Create")]
    static void Create(Texture2D mono, int w, int h, TextureFormat fmt, bool mipmap, bool linear, IntPtr native) {
        if (native != IntPtr.Zero) {
            Registry.Add(new NTexture { Width = w, Height = h, Format = fmt, GlTex = (uint)native, External = true, Dirty = false, Readable = false, Wrap = TextureWrapMode.Clamp }, mono);
            return;
        }
        var t = new NTexture { Width = w, Height = h, Format = fmt, MipCount = mipmap ? 2 : 1, Linear = linear, Pixels = new byte[w * h * 4] };
        for (int i = 3; i < t.Pixels.Length; i += 4) t.Pixels[i] = 255;   // Unity initialises new textures to opaque
        Registry.Add(t, mono);
    }

    [Icall("UnityEngine.Texture2D::UpdateExternalTexture")]
    static void UpdateExternal(Texture2D tex, IntPtr native) { var t = T(tex); t.GlTex = (uint)native; t.External = true; }

    [Icall("UnityEngine.Texture2D::get_format")] static TextureFormat Fmt(Texture2D t) => T(t).Format;
    [Icall("UnityEngine.Texture2D::get_mipmapCount")] static int Mips(Texture2D t) => T(t).MipCount;
    [Icall("UnityEngine.Texture2D::get_whiteTexture")] static Texture2D WhiteTex() => (Texture2D)Textures.White.Managed!;
    [Icall("UnityEngine.Texture2D::get_blackTexture")] static Texture2D BlackTex() => (Texture2D)Textures.Black.Managed!;

    static Color GetPx(NTexture t, int x, int y) {
        t.EnsurePixels();
        x = Wrap(x, t.Width, t.Wrap); y = Wrap(y, t.Height, t.Wrap);
        int i = (y * t.Width + x) * 4;
        var p = t.Pixels!;
        return new Color(p[i] / 255f, p[i + 1] / 255f, p[i + 2] / 255f, p[i + 3] / 255f);
    }

    static int Wrap(int v, int n, TextureWrapMode m) => m == TextureWrapMode.Clamp ? Math.Clamp(v, 0, n - 1) : ((v % n) + n) % n;

    static byte B(float f) => (byte)Math.Clamp((int)(f * 255f + 0.5f), 0, 255);

    [Icall("UnityEngine.Texture2D::INTERNAL_CALL_SetPixel")]
    static void SetPixel(Texture2D tex, int x, int y, ref Color c) {
        var t = T(tex);
        if (x < 0 || y < 0 || x >= t.Width || y >= t.Height) return;
        t.EnsurePixels();
        int i = (y * t.Width + x) * 4;
        t.Pixels![i] = B(c.r); t.Pixels[i + 1] = B(c.g); t.Pixels[i + 2] = B(c.b); t.Pixels[i + 3] = B(c.a);
    }

    [Icall("UnityEngine.Texture2D::GetPixel")] static Color GetPixel(Texture2D tex, int x, int y) => GetPx(T(tex), x, y);

    [Icall("UnityEngine.Texture2D::GetPixelBilinear")]
    static Color GetPixelBilinear(Texture2D tex, float u, float v) {
        var t = T(tex);
        float x = u * t.Width - 0.5f, y = v * t.Height - 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        Color a = GetPx(t, x0, y0), b = GetPx(t, x0 + 1, y0), c = GetPx(t, x0, y0 + 1), d = GetPx(t, x0 + 1, y0 + 1);
        Color L(Color p, Color q, float s) => new(p.r + (q.r - p.r) * s, p.g + (q.g - p.g) * s, p.b + (q.b - p.b) * s, p.a + (q.a - p.a) * s);
        return L(L(a, b, fx), L(c, d, fx), fy);
    }

    [Icall("UnityEngine.Texture2D::SetPixels")]
    static void SetPixels(Texture2D tex, int x, int y, int bw, int bh, Color[] colors, int mip) {
        var t = T(tex);
        if (mip != 0) return;
        t.EnsurePixels();
        for (int j = 0; j < bh; j++)
            for (int i = 0; i < bw; i++) {
                int px = x + i, py = y + j;
                if (px < 0 || py < 0 || px >= t.Width || py >= t.Height) continue;
                var c = colors[j * bw + i];
                int q = (py * t.Width + px) * 4;
                t.Pixels![q] = B(c.r); t.Pixels[q + 1] = B(c.g); t.Pixels[q + 2] = B(c.b); t.Pixels[q + 3] = B(c.a);
            }
    }

    [Icall("UnityEngine.Texture2D::SetAllPixels32")]
    static unsafe void SetAllPixels32(Texture2D tex, Color32[] colors, int mip) {
        var t = T(tex);
        if (mip != 0) return;
        t.Pixels ??= new byte[t.Width * t.Height * 4];
        int n = Math.Min(colors.Length * 4, t.Pixels.Length);
        fixed (Color32* src = colors) fixed (byte* dst = t.Pixels) Buffer.MemoryCopy(src, dst, t.Pixels.Length, n);
    }

    [Icall("UnityEngine.Texture2D::SetBlockOfPixels32")]
    static void SetBlockOfPixels32(Texture2D tex, int x, int y, int bw, int bh, Color32[] colors, int mip) {
        var t = T(tex);
        t.EnsurePixels();
        for (int j = 0; j < bh; j++)
            for (int i = 0; i < bw; i++) {
                int px = x + i, py = y + j;
                if (px < 0 || py < 0 || px >= t.Width || py >= t.Height) continue;
                var c = colors[j * bw + i];
                int q = (py * t.Width + px) * 4;
                t.Pixels![q] = c.r; t.Pixels[q + 1] = c.g; t.Pixels[q + 2] = c.b; t.Pixels[q + 3] = c.a;
            }
    }

    [Icall("UnityEngine.Texture2D::GetPixels")]
    static Color[] GetPixels(Texture2D tex, int x, int y, int bw, int bh, int mip) {
        var t = T(tex);
        var r = new Color[bw * bh];
        for (int j = 0; j < bh; j++) for (int i = 0; i < bw; i++) r[j * bw + i] = GetPx(t, x + i, y + j);
        return r;
    }

    [Icall("UnityEngine.Texture2D::GetPixels32")]
    static unsafe Color32[] GetPixels32(Texture2D tex, int mip) {
        var t = T(tex);
        t.EnsurePixels();
        var r = new Color32[t.Width * t.Height];
        fixed (Color32* dst = r) fixed (byte* src = t.Pixels) Buffer.MemoryCopy(src, dst, r.Length * 4, r.Length * 4);
        return r;
    }

    [Icall("UnityEngine.Texture2D::Apply")]
    static void Apply(Texture2D tex, bool updateMips, bool noLongerReadable) {
        var t = T(tex);
        t.Dirty = true;
        if (noLongerReadable) t.Readable = false;
    }

    [Icall("UnityEngine.Texture2D::LoadImage")]
    static bool LoadImage(Texture2D tex, byte[] data) {
        var t = T(tex);
        var px = Textures.DecodeImage(data, out int w, out int h);
        if (px == null) {
            // Unity shows a red '?' texture; keep the texture and report failure
            Log.Once("loadimage", "Texture2D.LoadImage: unsupported image data");
            return false;
        }
        t.Width = w; t.Height = h; t.Pixels = px; t.Format = TextureFormat.RGBA32; t.Dirty = true;
        return true;
    }

    [Icall("UnityEngine.Texture2D::LoadRawTextureData")]
    static void LoadRaw(Texture2D tex, byte[] data) {
        var t = T(tex);
        t.Pixels = Textures.Decode(data, 0, data.Length, t.Width, t.Height, t.Format);
        t.Dirty = true;
    }

    [Icall("UnityEngine.Texture2D::GetRawTextureData")]
    static byte[] GetRaw(Texture2D tex) { var t = T(tex); t.EnsurePixels(); return (byte[])t.Pixels!.Clone(); }

    [Icall("UnityEngine.Texture2D::Resize")]
    static bool Resize(Texture2D tex, int w, int h, TextureFormat fmt, bool mipmap) {
        var t = T(tex);
        t.Width = w; t.Height = h; t.Format = fmt; t.MipCount = mipmap ? 2 : 1; t.Pixels = new byte[w * h * 4]; t.Dirty = true;
        return true;
    }

    [Icall("UnityEngine.Texture2D::Internal_ResizeWH")]
    static bool ResizeWH(Texture2D tex, int w, int h) { var t = T(tex); return Resize(tex, w, h, t.Format, t.MipCount > 1); }

    [Icall("UnityEngine.Texture2D::INTERNAL_CALL_Compress")] static void Compress(Texture2D t, bool hq) { }

    [Icall("UnityEngine.Texture2D::INTERNAL_CALL_ReadPixels")]
    static unsafe void ReadPixels(Texture2D tex, ref Rect src, int dx, int dy, bool mips) {
        var t = T(tex);
        t.EnsurePixels();
        int w = (int)src.width, h = (int)src.height;
        var buf = new byte[w * h * 4];
        if (Device.Ready) {
            GL.glPixelStorei(GL.PACK_ALIGNMENT, 1);
            fixed (byte* p = buf) GL.glReadPixels((int)src.x, (int)src.y, w, h, GL.RGBA, GL.UNSIGNED_BYTE, p);
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                int tx = dx + x, ty = dy + y;
                if (tx < 0 || ty < 0 || tx >= t.Width || ty >= t.Height) continue;
                Buffer.BlockCopy(buf, (y * w + x) * 4, t.Pixels!, (ty * t.Width + tx) * 4, 4);
            }
    }

    [Icall("UnityEngine.Texture2D::EncodeToPNG")]
    static byte[] EncodeToPNG(Texture2D tex) {
        var t = T(tex);
        t.EnsurePixels();
        string tmp = Path.Combine(Path.GetTempPath(), "ff9enc.png");
        Png.Write(tmp, t.Pixels!, t.Width, t.Height, flipY: true);
        return File.ReadAllBytes(tmp);
    }

    [Icall("UnityEngine.Texture3D::Internal_Create")]
    static void Create3D(Texture3D mono, int w, int h, int d, TextureFormat fmt, bool mip) =>
        Registry.Add(new NTexture { Width = w, Height = h * d, Format = fmt, Pixels = new byte[w * h * d * 4] }, mono);
    [Icall("UnityEngine.Texture3D::SetPixels")] static void SetPixels3D(Texture3D t, Color[] c, int mip) => Log.Once("Texture3D.SetPixels");
    [Icall("UnityEngine.Texture3D::Apply")] static void Apply3D(Texture3D t, bool m) { }

    // ---- RenderTexture ----
    static NRenderTexture RT(RenderTexture r) => (NRenderTexture)Textures.Of(r);

    [Icall("UnityEngine.RenderTexture::Internal_CreateRenderTexture")]
    static void CreateRT(RenderTexture rt) => Registry.Add(new NRenderTexture { Width = 256, Height = 256, Readable = false }, rt);

    static readonly List<NRenderTexture> temps = new();

    [Icall("UnityEngine.RenderTexture::GetTemporary")]
    static RenderTexture GetTemporary(int w, int h, int depth, RenderTextureFormat fmt, RenderTextureReadWrite rw, int aa) {
        foreach (var t in temps)
            if (!t.TempInUse && t.Width == w && t.Height == h && t.Depth == depth && Registry.Get(t.Managed) == t) { t.TempInUse = true; return (RenderTexture)t.Managed!; }
        var m = (RenderTexture)RuntimeHelpers.GetUninitializedObject(typeof(RenderTexture));
        var n = Registry.Add(new NRenderTexture { Width = w, Height = h, Depth = depth, RtFormat = fmt, Temporary = true, TempInUse = true, Readable = false, Name = "TempBuffer" }, m);
        temps.Add(n);
        return m;
    }

    [Icall("UnityEngine.RenderTexture::ReleaseTemporary")]
    static void ReleaseTemporary(RenderTexture rt) { if (Textures.OfOrNull(rt) is NRenderTexture n) n.TempInUse = false; }

    /// For the renderer (a camera's OnRenderImage): RenderTexture.GetTemporary / ReleaseTemporary.
    public static RenderTexture Temporary(int w, int h, int depth) => GetTemporary(w, h, depth, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 1);
    public static void ReleaseTemp(RenderTexture rt) => ReleaseTemporary(rt);

    [Icall("UnityEngine.RenderTexture::Internal_GetWidth")] static int RW(RenderTexture r) => RT(r).Width;
    [Icall("UnityEngine.RenderTexture::Internal_SetWidth")] static void SRW(RenderTexture r, int v) => RT(r).Width = v;
    [Icall("UnityEngine.RenderTexture::Internal_GetHeight")] static int RH(RenderTexture r) => RT(r).Height;
    [Icall("UnityEngine.RenderTexture::Internal_SetHeight")] static void SRH(RenderTexture r, int v) => RT(r).Height = v;
    [Icall("UnityEngine.RenderTexture::Internal_SetSRGBReadWrite")] static void SRGB(RenderTexture r, bool v) { }
    [Icall("UnityEngine.RenderTexture::get_depth")] static int GD(RenderTexture r) => RT(r).Depth;
    [Icall("UnityEngine.RenderTexture::set_depth")] static void SD(RenderTexture r, int v) => RT(r).Depth = v;
    [Icall("UnityEngine.RenderTexture::get_format")] static RenderTextureFormat GFmt(RenderTexture r) => RT(r).RtFormat;
    [Icall("UnityEngine.RenderTexture::set_format")] static void SFmt(RenderTexture r, RenderTextureFormat v) => RT(r).RtFormat = v;
    [Icall("UnityEngine.RenderTexture::set_enableRandomWrite")] static void RandomWrite(RenderTexture r, bool v) { }
    [Icall("UnityEngine.RenderTexture::set_antiAliasing")] static void AA(RenderTexture r, int v) { }
    [Icall("UnityEngine.RenderTexture::INTERNAL_CALL_Create")] static bool CreateIcall(RenderTexture r) => RT(r).Create() || true;
    [Icall("UnityEngine.RenderTexture::INTERNAL_CALL_Release")] static void Release(RenderTexture r) => RT(r).Release();
    [Icall("UnityEngine.RenderTexture::INTERNAL_CALL_IsCreated")] static bool IsCreated(RenderTexture r) => RT(r).Created;
    [Icall("UnityEngine.RenderTexture::INTERNAL_CALL_DiscardContents")] static void Discard(RenderTexture r) { }
    [Icall("UnityEngine.RenderTexture::INTERNAL_CALL_MarkRestoreExpected")] static void Restore(RenderTexture r) { }

    [Icall("UnityEngine.RenderTexture::GetColorBuffer")]
    static void ColorBuffer(RenderTexture r, ref RenderBuffer rb) => SetRB(ref rb, RT(r).Id);

    [Icall("UnityEngine.RenderTexture::GetDepthBuffer")]
    static void DepthBuffer(RenderTexture r, ref RenderBuffer rb) => SetRB(ref rb, RT(r).Id);

    static unsafe void SetRB(ref RenderBuffer rb, int id) {
        // RenderBuffer { int m_RenderTextureInstanceID; IntPtr m_BufferPtr; }
        fixed (RenderBuffer* p = &rb) *(int*)p = id;
    }

    [Icall("UnityEngine.RenderTexture::get_active")]
    static RenderTexture? GetActive() => RenderTargets.Active?.Managed as RenderTexture;

    [Icall("UnityEngine.RenderTexture::set_active")]
    static void SetActive(RenderTexture? r) => RenderTargets.Set(r == null ? null : RT(r));
}
