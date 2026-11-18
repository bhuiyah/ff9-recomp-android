// Materials: shader + property sheet (floats, colours/vectors, textures with scale/offset,
// matrices) + keywords + render queue.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using FF9Engine.Assets;
using FF9Engine.Core;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FF9Engine.Gfx;

public sealed class NMaterial : NObject, ICloneable {
    public UnityEngine.Shader? ShaderObj;
    public readonly Dictionary<int, float> Floats = new();
    public readonly Dictionary<int, Vector4> Vectors = new();
    public readonly Dictionary<int, Texture?> Textures = new();
    public readonly Dictionary<int, Vector4> TexST = new();     // (scale.x, scale.y, offset.x, offset.y)
    public readonly Dictionary<int, Matrix4x4> Matrices = new();
    public readonly HashSet<string> Keywords = new();
    public int CustomQueue = -1;

    public NShader? Shader => Shaders.Of(ShaderObj);
    public int RenderQueue => CustomQueue >= 0 ? CustomQueue : Shader?.RenderQueue ?? 2000;

    public float GetFloat(int id) => Floats.TryGetValue(id, out var f) ? f : Vectors.TryGetValue(id, out var v) ? v.x : 0;

    public object Clone() {
        var m = new NMaterial { ShaderObj = ShaderObj, CustomQueue = CustomQueue, Name = Name };
        foreach (var kv in Floats) m.Floats[kv.Key] = kv.Value;
        foreach (var kv in Vectors) m.Vectors[kv.Key] = kv.Value;
        foreach (var kv in Textures) m.Textures[kv.Key] = kv.Value;
        foreach (var kv in TexST) m.TexST[kv.Key] = kv.Value;
        foreach (var kv in Matrices) m.Matrices[kv.Key] = kv.Value;
        foreach (var k in Keywords) m.Keywords.Add(k);
        return m;
    }

    public void CopyFrom(NMaterial o) {
        Floats.Clear(); Vectors.Clear(); Textures.Clear(); TexST.Clear(); Matrices.Clear(); Keywords.Clear();
        foreach (var kv in o.Floats) Floats[kv.Key] = kv.Value;
        foreach (var kv in o.Vectors) Vectors[kv.Key] = kv.Value;
        foreach (var kv in o.Textures) Textures[kv.Key] = kv.Value;
        foreach (var kv in o.TexST) TexST[kv.Key] = kv.Value;
        foreach (var kv in o.Matrices) Matrices[kv.Key] = kv.Value;
        foreach (var k in o.Keywords) Keywords.Add(k);
    }

    /// Defaults for properties the shader declares but the material does not set.
    public bool DefaultsApplied;

    public void ApplyShaderDefaults() {
        var sh = Shader;
        if (sh == null) return;
        DefaultsApplied = true;
        foreach (var (name, (type, def)) in sh.Properties) {
            int id = PropIds.Id(name);
            string t = type.ToLowerInvariant();
            if (t is "color" or "vector") {
                if (!Vectors.ContainsKey(id)) Vectors[id] = ParseVec(def);
            } else if (t is "2d" or "cube" or "3d" or "rect") {
                if (!TexST.ContainsKey(id)) TexST[id] = new Vector4(1, 1, 0, 0);
            } else if (!Floats.ContainsKey(id) && float.TryParse(def.Split(' ')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                Floats[id] = f;
        }
    }

    static Vector4 ParseVec(string s) {
        s = s.Trim().TrimStart('(').TrimEnd(')');
        var p = s.Split(',').Select(x => float.TryParse(x.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0).ToArray();
        return new Vector4(p.Length > 0 ? p[0] : 0, p.Length > 1 ? p[1] : 0, p.Length > 2 ? p[2] : 0, p.Length > 3 ? p[3] : 0);
    }
}

public static class Materials {
    public static void Register() {
        AssetDB.Readers[ClassIds.Material] = Read;
    }

    /// Material (5.2): name, shader, keywords string, lightmapFlags, customRenderQueue, stringTagMap,
    /// saved properties (texEnvs, floats, colors).
    static void Read(NAsset a, BinReader r) {
        var m = new NMaterial();
        a.Data = m;
        a.Name = m.Name = r.String();
        var (sfi, spi) = r.PPtr();
        var sf = AssetDB.External(a.File!, sfi);
        if (sf != null && spi != 0) m.ShaderObj = AssetDB.Load(sf, spi) as UnityEngine.Shader;
        foreach (var k in r.String().Split(' ', StringSplitOptions.RemoveEmptyEntries)) m.Keywords.Add(k);
        r.U32();
        m.CustomQueue = r.I32();
        int tags = r.I32();
        for (int i = 0; i < tags; i++) { r.String(); r.String(); }
        int nt = r.I32();
        for (int i = 0; i < nt; i++) {
            int id = PropIds.Id(r.String());
            var (tfi, tpi) = r.PPtr();
            var scale = r.V2(); var offset = r.V2();
            var tf = AssetDB.External(a.File!, tfi);
            m.Textures[id] = tf != null && tpi != 0 ? AssetDB.Load(tf, tpi) as Texture : null;
            m.TexST[id] = new Vector4(scale.x, scale.y, offset.x, offset.y);
        }
        int nf = r.I32();
        for (int i = 0; i < nf; i++) { int id = PropIds.Id(r.String()); m.Floats[id] = r.F32(); }
        int nc = r.I32();
        for (int i = 0; i < nc; i++) { int id = PropIds.Id(r.String()); var c = r.Color(); m.Vectors[id] = new Vector4(c.r, c.g, c.b, c.a); }
        if (m.CustomQueue == -1 && m.Shader == null) { }
        m.ApplyShaderDefaults();
    }

    public static NMaterial? Of(Material? mat) => Registry.Get(mat) switch {
        NAsset { Data: NMaterial m } => m,
        NMaterial m => m,
        _ => null,
    };

    static NMaterial M(Material m) => Of(m) ?? throw new NullReferenceException("material is null");

    [Icall("UnityEngine.Material::Internal_CreateWithShader")]
    static void CreateWithShader(Material mono, UnityEngine.Shader shader) {
        var m = Registry.Add(new NMaterial { ShaderObj = shader, Name = "" }, mono);
        m.ApplyShaderDefaults();
    }

    [Icall("UnityEngine.Material::Internal_CreateWithMaterial")]
    static void CreateWithMaterial(Material mono, Material source) {
        var src = M(source);
        var m = (NMaterial)src.Clone();
        m.Name = src.Name;
        Registry.Add(m, mono);
    }

    [Icall("UnityEngine.Material::Internal_CreateWithString")]
    static void CreateWithString(Material mono, string contents) {
        var s = new NShader { Source = contents };
        Shaders.Parse(s);
        var sm = (UnityEngine.Shader)RuntimeHelpers.GetUninitializedObject(typeof(UnityEngine.Shader));
        Registry.Add(new NAsset { Data = s, Name = s.Name, IsAsset = true, ClassId = ClassIds.Shader }, sm);
        Shaders.ByName[s.Name] = s;
        var m = Registry.Add(new NMaterial { ShaderObj = sm }, mono);
        m.ApplyShaderDefaults();
    }

    [Icall("UnityEngine.Material::get_shader")] static UnityEngine.Shader? GetShader(Material m) => M(m).ShaderObj;
    [Icall("UnityEngine.Material::set_shader")] static void SetShader(Material m, UnityEngine.Shader s) { long pt = Core.Prof.T0(); var x = M(m); x.ShaderObj = s; x.ApplyShaderDefaults(); Core.Prof.Call(5, pt); }
    [Icall("UnityEngine.Material::INTERNAL_CALL_SetColor")] static void SetColor(Material m, int id, ref Color c) => M(m).Vectors[id] = new Vector4(c.r, c.g, c.b, c.a);
    [Icall("UnityEngine.Material::GetColor")] static Color GetColor(Material m, int id) { var v = M(m).Vectors.GetValueOrDefault(id); return new Color(v.x, v.y, v.z, v.w); }
    [Icall("UnityEngine.Material::INTERNAL_CALL_SetVector")] static void SetVector(Material m, int id, ref Vector4 v) => M(m).Vectors[id] = v;
    [Icall("UnityEngine.Material::GetVector")] static Vector4 GetVector(Material m, int id) => M(m).Vectors.GetValueOrDefault(id);
    [Icall("UnityEngine.Material::SetTexture")]
    static void SetTexture(Material m, int id, Texture t) { var x = M(m); x.Textures[id] = t; if (!x.TexST.ContainsKey(id)) x.TexST[id] = new Vector4(1, 1, 0, 0); }
    [Icall("UnityEngine.Material::GetTexture")] static Texture? GetTexture(Material m, int id) => M(m).Textures.GetValueOrDefault(id);
    [Icall("UnityEngine.Material::Internal_GetTextureOffset")]
    static void GetTexOffset(Material m, string name, ref Vector2 o) { var st = M(m).TexST.GetValueOrDefault(PropIds.Id(name), new Vector4(1, 1, 0, 0)); o = new Vector2(st.z, st.w); }
    [Icall("UnityEngine.Material::Internal_GetTextureScale")]
    static void GetTexScale(Material m, string name, ref Vector2 o) { var st = M(m).TexST.GetValueOrDefault(PropIds.Id(name), new Vector4(1, 1, 0, 0)); o = new Vector2(st.x, st.y); }
    [Icall("UnityEngine.Material::INTERNAL_CALL_SetTextureOffset")]
    static void SetTexOffset(Material m, string name, ref Vector2 o) { var x = M(m); int id = PropIds.Id(name); var st = x.TexST.GetValueOrDefault(id, new Vector4(1, 1, 0, 0)); x.TexST[id] = new Vector4(st.x, st.y, o.x, o.y); }
    [Icall("UnityEngine.Material::INTERNAL_CALL_SetTextureScale")]
    static void SetTexScale(Material m, string name, ref Vector2 s) { var x = M(m); int id = PropIds.Id(name); var st = x.TexST.GetValueOrDefault(id, new Vector4(1, 1, 0, 0)); x.TexST[id] = new Vector4(s.x, s.y, st.z, st.w); }
    [Icall("UnityEngine.Material::INTERNAL_CALL_SetMatrix")] static void SetMatrix(Material m, int id, ref Matrix4x4 v) => M(m).Matrices[id] = v;
    [Icall("UnityEngine.Material::GetMatrix")] static Matrix4x4 GetMatrix(Material m, int id) => M(m).Matrices.GetValueOrDefault(id);
    [Icall("UnityEngine.Material::SetFloat")] static void SetFloat(Material m, int id, float v) => M(m).Floats[id] = v;
    [Icall("UnityEngine.Material::GetFloat")] static float GetFloat(Material m, int id) => M(m).GetFloat(id);
    [Icall("UnityEngine.Material::SetBuffer")] static void SetBuffer(Material m, string n, ComputeBuffer b) => Log.Once("Material.SetBuffer");

    [Icall("UnityEngine.Material::HasProperty")]
    static bool HasProperty(Material m, int id) {
        var x = M(m);
        if (x.Floats.ContainsKey(id) || x.Vectors.ContainsKey(id) || x.TexST.ContainsKey(id) || x.Matrices.ContainsKey(id)) return true;
        return x.Shader?.Properties.ContainsKey(PropIds.Name(id)) ?? false;
    }

    [Icall("UnityEngine.Material::get_passCount")] static int PassCount(Material m) => M(m).Shader?.Passes.Count ?? 0;
    [Icall("UnityEngine.Material::SetPass")] static bool SetPass(Material m, int pass) => Pipeline.SetPass(M(m), pass);
    [Icall("UnityEngine.Material::get_renderQueue")] static int GetQueue(Material m) => M(m).RenderQueue;
    [Icall("UnityEngine.Material::set_renderQueue")] static void SetQueue(Material m, int v) => M(m).CustomQueue = v;
    [Icall("UnityEngine.Material::CopyPropertiesFromMaterial")] static void CopyProps(Material m, Material o) => M(m).CopyFrom(M(o));
    [Icall("UnityEngine.Material::EnableKeyword")] static void EnableKeyword(Material m, string k) => M(m).Keywords.Add(k);
    [Icall("UnityEngine.Material::DisableKeyword")] static void DisableKeyword(Material m, string k) => M(m).Keywords.Remove(k);
    [Icall("UnityEngine.Material::IsKeywordEnabled")] static bool IsKeywordEnabled(Material m, string k) => M(m).Keywords.Contains(k);
    [Icall("UnityEngine.Material::get_shaderKeywords")] static string[] GetKeywords(Material m) => M(m).Keywords.ToArray();
    [Icall("UnityEngine.Material::set_shaderKeywords")] static void SetKeywords(Material m, string[] k) { var x = M(m); x.Keywords.Clear(); foreach (var s in k) x.Keywords.Add(s); }
    [Icall("UnityEngine.Material::GetTag")] static string GetTag(Material m, string tag, bool searchFallbacks, string def) => M(m).Shader?.Tags.GetValueOrDefault(tag, def) ?? def;
}
