// Unity Font: dynamic fonts render glyphs on demand into an atlas texture
// (RequestCharactersInTexture / GetCharacterInfo, as NGUI and uGUI use them); bitmap fonts keep
// their baked character rects.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FF9Engine.Assets;
using FF9Engine.Core;
using FF9Engine.Gfx;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FF9Engine.Text;

public sealed class NFont : NObject {
    public float LineSpacing, FontSizeF = 16, Ascent, Tracking = 1;
    public Material? MaterialObj;
    public Texture? TextureObj;
    public CharacterInfo[] BakedChars = Array.Empty<CharacterInfo>();
    public byte[]? Data;
    public string[] Names = Array.Empty<string>();
    public UObject[] Fallbacks = Array.Empty<UObject>();
    public FontFile? File;
    public bool Dynamic => Data != null && Data.Length > 0 || File != null;

    // atlas
    public NTexture? Atlas;
    int penX, penY, rowH;
    public readonly Dictionary<(char, int, FontStyle), CharacterInfo> Glyphs = new();

    public int FontSize => (int)FontSizeF;

    public void EnsureAtlas() {
        if (Atlas != null) return;
        Atlas = new NTexture { Width = 1024, Height = 1024, Format = TextureFormat.Alpha8, Pixels = new byte[1024 * 1024 * 4], Filter = FilterMode.Bilinear, Wrap = TextureWrapMode.Clamp, Name = "Font Texture",
            HideFlags = HideFlags.DontUnloadUnusedAsset };     // the font's own: held here, not through its managed object (Core/Unloader.cs)
        for (int i = 0; i < Atlas.Pixels.Length; i += 4) { Atlas.Pixels[i] = Atlas.Pixels[i + 1] = Atlas.Pixels[i + 2] = 255; }
        Registry.Add(Atlas, (UObject)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D)));
        TextureObj = (Texture)Atlas.Managed!;
        if (Materials.Of(MaterialObj) is { } m) m.Textures[PropIds.Id("_MainTex")] = TextureObj;
        penX = 1; penY = 1; rowH = 0;
    }

    /// Rasterizes a glyph into the atlas; on overflow the atlas grows and all glyphs are re-rendered
    /// (Unity then raises Font.textureRebuilt).
    public bool Add(char ch, int size, FontStyle style, out bool rebuilt) {
        rebuilt = false;
        if (File == null || size <= 0) return false;
        if (Glyphs.ContainsKey((ch, size, style))) return true;
        EnsureAtlas();
        int g = File.Glyph(ch);
        float scale = size;
        var bmp = File.Render(g, scale, out int w, out int h, out int left, out int top);
        int pad = 1;
        if (penX + w + pad >= Atlas!.Width) { penX = 1; penY += rowH + pad; rowH = 0; }
        if (penY + h + pad >= Atlas.Height) {
            Grow();
            rebuilt = true;
            return Add(ch, size, style, out _);
        }
        // atlas rows are bottom-up: glyph row r (top-down) goes to texture row (H - 1 - (penY + r))
        int texH = Atlas.Height;
        for (int r = 0; r < h; r++) {
            int ty = texH - 1 - (penY + r);
            for (int c = 0; c < w; c++) Atlas.Pixels![(ty * Atlas.Width + penX + c) * 4 + 3] = bmp[r * w + c];
        }
        Atlas.Dirty = true;
        float adv = File.Advance(g) * size / (float)File.UnitsPerEm;
        if ((style & FontStyle.Bold) != 0) adv += 1;
        var ci = new CharacterInfo();
        ci.index = ch;
        ci.size = size;
        ci.style = style;
        ci.width = adv;
        // uv: x = left, y = top edge in texture space (bottom-up), height negative (down)
        float u0 = penX / (float)Atlas.Width, u1 = (penX + w) / (float)Atlas.Width;
        float vTop = (texH - penY) / (float)texH, vBot = (texH - penY - h) / (float)texH;
        // Unity 5: uv from the bottom-left corner (positive height); vert.y relative to the ascent
        // (maxY = ascent + vert.y), vert.height negative (downwards)
        int ascent = (int)MathF.Round(File.Ascender * size / (float)File.UnitsPerEm);
        ci.uv = new Rect(u0, vBot, u1 - u0, vTop - vBot);
        ci.vert = new Rect(left, top - ascent, w, -h);
        ci.flipped = false;
        SetAscent(ref ci, ascent);
        Glyphs[(ch, size, style)] = ci;
        penX += w + pad;
        rowH = Math.Max(rowH, h);
        return true;
    }

    static readonly FieldInfo? ascentField = typeof(CharacterInfo).GetField("ascent", BindingFlags.Instance | BindingFlags.NonPublic);
    static void SetAscent(ref CharacterInfo ci, int a) { object box = ci; ascentField?.SetValue(box, a); ci = (CharacterInfo)box; }

    void Grow() {
        var keys = Glyphs.Keys.ToList();
        int nw = Atlas!.Width < 4096 ? Atlas.Width * 2 : Atlas.Width;
        if (nw == Atlas.Width) keys.Clear();     // full at 4096: start again with only new glyphs
        Atlas.Width = Atlas.Height = nw;
        Atlas.Pixels = new byte[nw * nw * 4];
        for (int i = 0; i < Atlas.Pixels.Length; i += 4) { Atlas.Pixels[i] = Atlas.Pixels[i + 1] = Atlas.Pixels[i + 2] = 255; }
        Atlas.Dirty = true;
        Glyphs.Clear();
        penX = 1; penY = 1; rowH = 0;
        foreach (var (c, s, st) in keys) Add(c, s, st, out _);
        Fonts.Rebuilt(this);
    }
}

public static class Fonts {
    public static void Register() => AssetDB.Readers[ClassIds.Font] = Read;

    public static NFont? Of(Font? f) => Registry.Get(f) switch { NAsset { Data: NFont x } => x, NFont x => x, _ => null };
    static NFont F(Font f) => Of(f) ?? throw new NullReferenceException("font is null");

    /// Font (5.2, from its type tree): name, asciiStartOffset, kerning, lineSpacing, characterSpacing,
    /// characterPadding, convertCase, defaultMaterial, characterRects, texture, kerningValues,
    /// pixelScale, fontData, fontSize, ascent, defaultStyle, fontNames, fallbackFonts, renderingMode.
    static void Read(NAsset a, BinReader r) {
        if (Environment.GetEnvironmentVariable("FF9_DUMP_TREE") is { } dt && a.File != null) {
            var info = a.File.Objects[a.PathId];
            var tree = a.File.TreeOf(info);
            if (tree != null) foreach (var c in tree.Root.Children) Log.Info($"  Font tree: {c.Type} {c.Name} size={c.ByteSize} align={c.Align}");
        }
        var f = new NFont();
        a.Data = f;
        a.Name = f.Name = r.String();
        r.I32();                      // asciiStartOffset
        f.Tracking = r.F32();         // m_Kerning
        f.LineSpacing = r.F32();
        r.I32(); r.I32(); r.I32();    // characterSpacing, characterPadding, convertCase
        var (mfi, mpi) = r.PPtr();
        int nc = r.I32();
        var baked = new List<CharacterInfo>();
        for (int i = 0; i < nc; i++) {
            var ci = new CharacterInfo { index = (int)r.U32() };
            ci.uv = r.Rect(); ci.vert = r.Rect(); ci.width = r.F32();
            ci.flipped = r.Bool(); r.Align4();
            baked.Add(ci);
        }
        f.BakedChars = baked.ToArray();
        var (tfi, tpi) = r.PPtr();
        int nk = r.I32(); r.Skip(nk * 8L);
        r.F32(); r.Align4();          // pixelScale
        int nd = r.I32();
        if (nd > 0) f.Data = r.Bytes(nd);
        r.Align4();
        f.FontSizeF = r.F32();
        f.Ascent = r.F32();
        r.U32();
        int nn = r.I32();
        f.Names = new string[nn];
        for (int i = 0; i < nn; i++) f.Names[i] = r.String();
        var file = a.File!;
        var mf = AssetDB.External(file, mfi);
        if (mf != null && mpi != 0) f.MaterialObj = AssetDB.Load(mf, mpi) as Material;
        var tf = AssetDB.External(file, tfi);
        if (tf != null && tpi != 0) f.TextureObj = AssetDB.Load(tf, tpi) as Texture;
        if (f.Data != null) {
            try {
                f.File = new FontFile(f.Data);
                Log.Info($"font '{f.Name}': {(f.Data.Length / 1024)} KB, {f.File.NumGlyphs} glyphs, {f.File.UnitsPerEm} units/em");
            } catch (Exception e) { Log.Warn($"font '{f.Name}' data: {e.Message}"); }
        }
    }

    static readonly FieldInfo? rebuilt = typeof(Font).GetField("textureRebuilt", BindingFlags.Static | BindingFlags.NonPublic);
    static readonly FieldInfo? rebuildCb = typeof(Font).GetField("m_FontTextureRebuildCallback", BindingFlags.Instance | BindingFlags.NonPublic);

    public static void Rebuilt(NFont f) {
        if (f.Managed is not Font mf) return;
        try {
            (rebuilt?.GetValue(null) as Action<Font>)?.Invoke(mf);
            (rebuildCb?.GetValue(mf) as Delegate)?.DynamicInvoke();
        } catch (Exception e) { Log.Exception(e.InnerException ?? e, "Font.textureRebuilt"); }
    }

    [Icall("UnityEngine.Font::Internal_CreateFont")]
    static void CreateFont(Font f, string name) => Registry.Add(new NFont { Name = name ?? "" }, f);

    [Icall("UnityEngine.Font::Internal_CreateDynamicFont")]
    static void CreateDynamic(Font f, string[] names, int size) {
        var n = Registry.Add(new NFont { Name = names?.FirstOrDefault() ?? "", Names = names ?? Array.Empty<string>(), FontSizeF = size }, f);
        // OS fonts: none on Android; borrow the first loaded dynamic font's outlines
        foreach (var o in Registry.All) if (o is NAsset { Data: NFont x } && x.File != null) { n.File = x.File; break; }
        n.MaterialObj = DefaultFontMaterial();
    }

    static Material? fontMat;
    static Material? DefaultFontMaterial() {
        if (fontMat != null) return fontMat;
        var sh = Shaders.Find("GUI/Text Shader") ?? Shaders.Find("UI/Default");
        if (sh == null) return null;
        foreach (var n in Registry.All)
            if (n is NAsset { Data: NShader s } && s == sh) { fontMat = new Material((UnityEngine.Shader)n.Managed!); break; }
        return fontMat;
    }

    [Icall("UnityEngine.Font::GetOSInstalledFontNames")] static string[] OSFonts() => Array.Empty<string>();

    [Icall("UnityEngine.Font::get_material")]
    static Material? GetMaterial(Font f) {
        var x = F(f);
        if (x.MaterialObj == null && x.Dynamic) x.MaterialObj = DefaultFontMaterial() is { } m ? (Material)Cloner.Clone(m, null, null) : null;
        if (x.Dynamic) { x.EnsureAtlas(); if (Materials.Of(x.MaterialObj) is { } nm) nm.Textures[PropIds.Id("_MainTex")] = x.TextureObj; }
        return x.MaterialObj;
    }

    [Icall("UnityEngine.Font::set_material")] static void SetMaterial(Font f, Material m) => F(f).MaterialObj = m;
    [Icall("UnityEngine.Font::HasCharacter")] static bool HasCharacter(Font f, char c) { var x = F(f); return x.File?.Has(c) ?? x.BakedChars.Any(b => b.index == c); }
    [Icall("UnityEngine.Font::get_fontNames")] static string[] Names(Font f) => F(f).Names;
    [Icall("UnityEngine.Font::set_fontNames")] static void SetNames(Font f, string[] n) => F(f).Names = n;
    [Icall("UnityEngine.Font::get_characterInfo")] static CharacterInfo[] GetChars(Font f) => F(f).BakedChars;
    [Icall("UnityEngine.Font::set_characterInfo")] static void SetChars(Font f, CharacterInfo[] c) => F(f).BakedChars = c;
    [Icall("UnityEngine.Font::get_dynamic")] static bool IsDynamic(Font f) => F(f).Dynamic;
    [Icall("UnityEngine.Font::get_fontSize")] static int FontSize(Font f) => F(f).FontSize;

    [Icall("UnityEngine.Font::get_ascent")]
    static int Ascent(Font f) { var x = F(f); return x.File != null ? (int)MathF.Round(x.File.Ascender * x.FontSize / (float)x.File.UnitsPerEm) : (int)x.Ascent; }

    [Icall("UnityEngine.Font::get_lineHeight")]
    static int LineHeight(Font f) {
        var x = F(f);
        if (x.File == null) return (int)x.LineSpacing;
        return (int)MathF.Round((x.File.Ascender - x.File.Descender + x.File.LineGap) * x.FontSize / (float)x.File.UnitsPerEm);
    }

    [Icall("UnityEngine.Font::RequestCharactersInTexture")]
    static void Request(Font f, string chars, int size, FontStyle style) {
        var x = F(f);
        if (!x.Dynamic || chars == null) return;
        if (size <= 0) size = x.FontSize;
        bool any = false;
        foreach (char c in chars) { x.Add(c, size, style, out bool rb); any |= rb; }
    }

    [Icall("UnityEngine.Font::GetCharacterInfo")]
    static bool GetCharacterInfo(Font f, char ch, ref CharacterInfo info, int size, FontStyle style) {
        var x = F(f);
        if (x.Dynamic) {
            if (size <= 0) size = x.FontSize;
            if (!x.Glyphs.TryGetValue((ch, size, style), out var ci)) return false;
            info = ci;
            return true;
        }
        foreach (var b in x.BakedChars) if (b.index == ch) { info = b; return true; }
        return false;
    }
}
