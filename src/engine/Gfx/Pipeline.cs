// Drawing: GL programs from shader passes, Unity's built-in uniforms, render state, the per-camera
// forward pass (sorting by queue), GL immediate mode and Graphics.Blit/DrawTexture/DrawMeshNow.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using FF9Engine.Core;
using FF9Engine.Scripting;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FF9Engine.Gfx;

public sealed class GpuProgram {
    public uint Id;
    public int VcLoc = -1, PcLoc = -1, AlphaRefLoc = -1;
    public int VcCount, PcCount;
    public SubProgram? Vp, Fp;
    public FixedFunction? FF;
    public readonly List<(int loc, int unit, string name)> Samplers = new();
    public bool Failed;
}

public static class Pipeline {
    static readonly Dictionary<(SubProgram?, SubProgram?, FixedFunction?, string?), GpuProgram> programs = new();
    public static int ProgramCount => programs.Count;

    // current matrices (Unity's "GL" state): model, view, projection
    public static Matrix4x4 Model = M.Identity, View = M.Identity, Proj = M.Identity;
    static readonly Stack<(Matrix4x4, Matrix4x4, Matrix4x4)> stack = new();
    public static NCamera? Camera;
    public static Vector3 CameraPos;

    static int AttribFor(string usage, int idx) => usage switch {
        "position" => NMesh.AttrPos, "normal" => NMesh.AttrNormal, "color" => NMesh.AttrColor, "tangent" => NMesh.AttrTangent,
        "texcoord" => idx switch { 0 => NMesh.AttrUV0, 1 => NMesh.AttrUV1, 2 => NMesh.AttrUV2, _ => NMesh.AttrUV3 },
        _ => 15,
    };

    static GpuProgram GetProgram(ShaderPass pass, SubProgram? vp, SubProgram? fp, string? alphaFunc) {
        var key = (vp, fp, vp == null ? pass.FF : null, alphaFunc);
        if (programs.TryGetValue(key, out var gp)) return gp;
        gp = new GpuProgram { Vp = vp, Fp = fp, FF = vp == null ? pass.FF : null };
        programs[key] = gp;
        try {
            string vsSrc, fsSrc;
            var inputs = new List<(int reg, string usage, int idx)>();
            if (vp != null && fp != null) {
                vp.Translated ??= D3D9ToGlsl.Translate(vp.Asm);
                var dims = fp.Textures.ToDictionary(t => t.reg, t => t.dim.ToUpperInvariant() == "CUBE" ? "CUBE" : t.dim.ToUpperInvariant() == "3D" ? "3D" : "2D");
                fp.Translated ??= D3D9ToGlsl.Translate(fp.Asm, dims);
                vsSrc = vp.Translated.Glsl;
                fsSrc = fp.Translated.Glsl;
                inputs.AddRange(vp.Translated.Inputs);
                gp.VcCount = vp.Translated.ConstCount;
                gp.PcCount = fp.Translated.ConstCount;
            } else {
                (vsSrc, fsSrc) = FixedFunctionGlsl.Generate(pass.FF ?? new FixedFunction());
                inputs.Add((0, "position", 0)); inputs.Add((1, "color", 0)); inputs.Add((2, "texcoord", 0));
            }
            if (alphaFunc != null) fsSrc = InjectAlphaTest(fsSrc, alphaFunc);
            vsSrc = MatchVaryings(vsSrc, fsSrc);
            if (DumpGlsl) Log.Info($"--- program {pass.Name} ---\n{vsSrc}\n{fsSrc}");
            if (DumpGlsl && Environment.GetEnvironmentVariable("FF9_DUMP_ASM") != null && vp != null && fp != null) Log.Info("--- asm ---\n" + vp.Asm + "\n--- ps asm ---\n" + fp.Asm);
            uint vs = GL.CompileShader(GL.VERTEX_SHADER, vsSrc, out var ve);
            uint fs = GL.CompileShader(GL.FRAGMENT_SHADER, fsSrc, out var fe);
            if (vs == 0 || fs == 0) {
                gp.Failed = true;
                Log.Warn($"shader compile failed: {ve}{fe}\n--- VS ---\n{vsSrc}\n--- FS ---\n{fsSrc}");
                return gp;
            }
            uint p = GL.glCreateProgram();
            GL.glAttachShader(p, vs);
            GL.glAttachShader(p, fs);
            foreach (var (reg, usage, idx) in inputs) GL.BindAttrib(p, (uint)AttribFor(usage, idx), vp != null ? $"a_v{reg}" : FixedFunctionGlsl.AttrName(usage));
            if (!GL.LinkProgram(p, out var le)) {
                gp.Failed = true;
                Log.Warn($"shader link failed: {le}\n--- VS ---\n{vsSrc}\n--- FS ---\n{fsSrc}");
                return gp;
            }
            GL.glDeleteShader(vs); GL.glDeleteShader(fs);
            gp.Id = p;
            gp.VcLoc = GL.UniformLocation(p, "vc");
            gp.PcLoc = GL.UniformLocation(p, "pc");
            gp.AlphaRefLoc = GL.UniformLocation(p, "uAlphaRef");
            GL.glUseProgram(p);
            if (fp != null) {
                foreach (var (reg, name, dim) in fp.Textures) {
                    int loc = GL.UniformLocation(p, "s" + reg);
                    if (loc >= 0) { GL.glUniform1i(loc, reg); gp.Samplers.Add((loc, reg, name)); }
                }
            } else if (pass.FF != null) {
                for (int i = 0; i < pass.FF.Stages.Count; i++) {
                    int loc = GL.UniformLocation(p, "s" + i);
                    if (loc >= 0) { GL.glUniform1i(loc, i); gp.Samplers.Add((loc, i, pass.FF.Stages[i].tex)); }
                }
            }
        } catch (Exception e) {
            gp.Failed = true;
            Log.Warn($"shader translation failed: {e.Message}");
        }
        return gp;
    }

    static string InjectAlphaTest(string fs, string func) {
        string cmp = func.ToLowerInvariant() switch {
            "greater" => ">", "gequal" => ">=", "less" => "<", "lequal" => "<=", "equal" => "==", "notequal" => "!=", "never" => "<", _ => ">=",
        };
        string test = func.ToLowerInvariant() == "never" ? "  discard;\n" : $"  if (!(oC0.a {cmp} uAlphaRef)) discard;\n";
        int last = fs.LastIndexOf('}');
        fs = fs[..last] + test + fs[last..];
        int main = fs.IndexOf("void main");
        return fs[..main] + "uniform float uAlphaRef;\n" + fs[main..];
    }

    /// GLSL ES requires every fragment input to be written by the vertex shader.
    static string MatchVaryings(string vs, string fs) {
        var outs = new HashSet<string>();
        foreach (var line in vs.Split('\n')) if (line.StartsWith("out vec4 ")) outs.Add(line[9..^1]);
        var add = new StringBuilder(); var init = new StringBuilder();
        foreach (var line in fs.Split('\n'))
            if (line.StartsWith("in vec4 ")) {
                string n = line[8..^1];
                if (!outs.Contains(n)) { add.Append($"out vec4 {n};\n"); init.Append($"  {n} = vec4(0.0);\n"); }
            }
        if (add.Length == 0) return vs;
        int main = vs.IndexOf("void main");
        vs = vs[..main] + add + vs[main..];
        int brace = vs.IndexOf('{', vs.IndexOf("void main")) + 1;
        return vs[..brace] + "\n" + init + vs[brace..];
    }

    // ---- uniforms ----
    static readonly float[] vbuf = new float[256 * 4], pbuf = new float[256 * 4];

    sealed class DrawCtx {
        public NMaterial? Mat;
        public NMaterialBlock? Block;
        public Matrix4x4 M, V, P;
    }

    static Vector4 Builtin(string name, DrawCtx c, out bool found) {
        found = true;
        float t = Clock.Now;
        int w = RenderTargets.Width, h = RenderTargets.Height;
        switch (name) {
            case "_Time": return new Vector4(t / 20, t, t * 2, t * 3);
            case "_SinTime": return new Vector4(MathF.Sin(t / 8), MathF.Sin(t / 4), MathF.Sin(t / 2), MathF.Sin(t));
            case "_CosTime": return new Vector4(MathF.Cos(t / 8), MathF.Cos(t / 4), MathF.Cos(t / 2), MathF.Cos(t));
            case "unity_DeltaTime": return new Vector4(Clock.Delta, 1 / Math.Max(Clock.Delta, 1e-5f), Clock.SmoothDelta, 1 / Math.Max(Clock.SmoothDelta, 1e-5f));
            case "_ScreenParams": return new Vector4(w, h, 1 + 1f / w, 1 + 1f / h);
            case "_ProjectionParams": {
                float n = Camera?.Near ?? 0.3f, f = Camera?.Far ?? 1000;
                return new Vector4(1, n, f, 1 / f);
            }
            case "_ZBufferParams": {
                float n = Camera?.Near ?? 0.3f, f = Camera?.Far ?? 1000;
                float x = 1 - f / n, y = f / n;
                return new Vector4(x, y, x / f, y / f);
            }
            case "_WorldSpaceCameraPos": return new Vector4(CameraPos.x, CameraPos.y, CameraPos.z, 1);
            case "glstate_lightmodel_ambient": { var a = Icalls.RenderState.AmbientLight; return new Vector4(a.r * 0.5f, a.g * 0.5f, a.b * 0.5f, a.a * 0.5f); }
            case "unity_AmbientSky": case "unity_AmbientEquator": case "unity_AmbientGround": { var a = Icalls.RenderState.AmbientLight; return new Vector4(a.r, a.g, a.b, a.a); }
            case "unity_FogColor": { var fc = Icalls.RenderState.FogColor; return new Vector4(fc.r, fc.g, fc.b, fc.a); }
            case "unity_FogParams": {
                // fog off (or an empty range): a factor of 1 everywhere, no fog (was: a divide by
                // zero that fogged the whole world map to the sky colour)
                float d = Icalls.RenderState.FogDensity, s = Icalls.RenderState.FogStart, e = Icalls.RenderState.FogEnd;
                if (!Icalls.RenderState.Fog || (Icalls.RenderState.FogMode == FogMode.Linear && e - s <= 1e-4f)) return new Vector4(0, 0, 0, 1);
                return new Vector4(d / 1.2011224f, d / 0.6931472f, Icalls.RenderState.FogMode == FogMode.Linear ? -1 / (e - s) : 0, Icalls.RenderState.FogMode == FogMode.Linear ? e / (e - s) : 0);
            }
            // Unity 5.2's separate fog values (the world map's legacy shaders read these): fog off
            // pushes the range far away so nothing is fogged
            case "unity_FogStart": { float v = Icalls.RenderState.Fog ? Icalls.RenderState.FogStart : 1e8f; return new Vector4(v, v, v, v); }
            case "unity_FogEnd": { float v = Icalls.RenderState.Fog ? Icalls.RenderState.FogEnd : 2e8f; return new Vector4(v, v, v, v); }
            case "unity_FogDensity": { float v = Icalls.RenderState.Fog ? Icalls.RenderState.FogDensity : 0; return new Vector4(v, v, v, v); }
            case "unity_ColorSpaceGrey": return new Vector4(0.5f, 0.5f, 0.5f, 0.5f);
            case "_LightColor0": case "unity_LightColor0": return new Vector4(0, 0, 0, 0);
            case "_WorldSpaceLightPos0": return new Vector4(0, 1, 0, 0);
            case "unity_Scale": return new Vector4(1, 1, 1, 1);
        }
        found = false;
        return default;
    }

    static bool MatrixFor(string name, DrawCtx c, out Matrix4x4 m) {
        switch (name) {
            case "glstate_matrix_mvp": m = M.Mul(c.P, M.Mul(c.V, c.M)); return true;
            case "glstate_matrix_modelview0": m = M.Mul(c.V, c.M); return true;
            case "glstate_matrix_projection": m = c.P; return true;
            case "glstate_matrix_invtrans_modelview0": m = Transpose(M.Inverse(M.Mul(c.V, c.M))); return true;
            case "glstate_matrix_transpose_modelview0": m = Transpose(M.Mul(c.V, c.M)); return true;
            case "_Object2World": m = c.M; return true;
            case "_World2Object": m = M.Inverse(c.M); return true;
            case "unity_MatrixV": m = c.V; return true;
            case "unity_MatrixVP": m = M.Mul(c.P, c.V); return true;
            case "glstate_matrix_texture0": case "glstate_matrix_texture1": case "glstate_matrix_texture2": case "glstate_matrix_texture3":
                m = M.Identity; return true;
        }
        if (c.Block != null && c.Block.Matrices.TryGetValue(PropIds.Id(name), out m)) return true;
        if (c.Mat?.Shader is { } msh && !msh.Properties.ContainsKey(name) && Globals.Matrices.TryGetValue(PropIds.Id(name), out m)) return true;
        if (c.Mat != null && c.Mat.Matrices.TryGetValue(PropIds.Id(name), out m)) return true;
        if (Globals.Matrices.TryGetValue(PropIds.Id(name), out m)) return true;
        m = M.Identity;
        return false;
    }

    static Matrix4x4 Transpose(Matrix4x4 a) {
        var r = new Matrix4x4();
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) r[i, j] = a[j, i];
        return r;
    }

    static Vector4 VectorFor(string name, DrawCtx c) => VectorFor(name, PropIds.Id(name), true, c);

    static Vector4 VectorFor(string name, int id, bool maybeBuiltin, DrawCtx c) {
        if (maybeBuiltin) {
            var b = Builtin(name, c, out bool found);
            if (found) return b;
        }
        // materials read before their shader was parsed get the Properties defaults on first use
        if (c.Mat != null && !c.Mat.DefaultsApplied) c.Mat.ApplyShaderDefaults();
        if (c.Block != null && c.Block.Vectors.TryGetValue(id, out var bv)) return bv;
        // a value the shader doesn't declare in Properties comes from the globals (Shader.SetGlobal*)
        // when one is set: a material's saved copy is stale (the field fades read their colours so)
        if (c.Mat?.Shader is { } msh && !msh.Properties.ContainsKey(name) && Globals.Vectors.TryGetValue(id, out var gv0)) return gv0;
        if (c.Mat != null) {
            if (c.Mat.Vectors.TryGetValue(id, out var mv)) return mv;
            if (c.Mat.Floats.TryGetValue(id, out var mf)) return new Vector4(mf, mf, mf, mf);
        }
        if (name.EndsWith("_ST")) {
            int tid = PropIds.Id(name[..^3]);
            if (c.Mat != null && c.Mat.TexST.TryGetValue(tid, out var st)) return st;
            return new Vector4(1, 1, 0, 0);
        }
        if (name.EndsWith("_TexelSize")) {
            var tex = TextureFor(name[..^10], c);
            if (tex != null) return new Vector4(1f / tex.Width, 1f / tex.Height, tex.Width, tex.Height);
            return new Vector4(1, 1, 1, 1);
        }
        if (Globals.Vectors.TryGetValue(id, out var gv)) return gv;
        return default;
    }

    static NTexture? TextureFor(string name, DrawCtx c) {
        int id = PropIds.Id(name);
        Texture? t = null;
        if (c.Block != null && c.Block.Textures.TryGetValue(id, out var bt)) t = bt;
        else if (c.Mat != null && c.Mat.Textures.TryGetValue(id, out var mt)) t = mt;
        else if (Globals.Textures.TryGetValue(id, out var gt)) t = gt;
        var n = Textures.OfOrNull(t);
        return n;
    }

    [ThreadStatic] public static bool LogConsts;
    static unsafe void FillConsts(SubProgram sp, float[] buf, int count, DrawCtx c) {
        Array.Clear(buf, 0, count * 4);
        if (sp.ConstIds == null) {
            var ids = new int[sp.Consts.Count];
            var bi = new bool[sp.Consts.Count];
            for (int i = 0; i < ids.Length; i++) {
                string n = sp.Consts[i].name;
                ids[i] = PropIds.Id(n);
                try { Builtin(n, new DrawCtx(), out bi[i]); } catch { bi[i] = true; }    // a name the built-in table knows
            }
            sp.ConstMaybeBuiltin = bi;
            sp.ConstIds = ids;
        }
        int ci = -1;
        foreach (var (kind, reg, name, rows) in sp.Consts) {
            ci++;
            if (kind == "Matrix") {
                MatrixFor(name, c, out var m);
                for (int r = 0; r < rows && reg + r < count; r++) {
                    int o = (reg + r) * 4;
                    buf[o] = m[r, 0]; buf[o + 1] = m[r, 1]; buf[o + 2] = m[r, 2]; buf[o + 3] = m[r, 3];
                }
            } else if (reg < count) {
                var v = VectorFor(name, sp.ConstIds[ci], sp.ConstMaybeBuiltin![ci], c);
                if (LogConsts) Log.Info($"    const {kind} c{reg} {name} = {v}");
                int o = reg * 4;
                buf[o] = v.x; buf[o + 1] = v.y; buf[o + 2] = v.z; buf[o + 3] = v.w;
            }
        }
    }

    // ---- state ----
    static uint BlendFactor(string s) => (s.Length <= 2 ? s : s.ToLowerInvariant()) switch {
        "zero" or "0" => GL.ZERO, "one" or "1" => GL.ONE, "srccolor" or "3" => GL.SRC_COLOR, "oneminussrccolor" or "6" => GL.ONE_MINUS_SRC_COLOR,
        "srcalpha" or "5" => GL.SRC_ALPHA, "oneminussrcalpha" or "10" => GL.ONE_MINUS_SRC_ALPHA, "dstcolor" or "2" => GL.DST_COLOR,
        "oneminusdstcolor" or "4" => GL.ONE_MINUS_DST_COLOR, "dstalpha" or "7" => GL.DST_ALPHA, "oneminusdstalpha" or "8" => GL.ONE_MINUS_DST_ALPHA,
        "srcalphasaturate" or "9" => GL.SRC_ALPHA_SATURATE, _ => GL.ONE,
    };

    static uint CompareFunc(string s) => (s.Length <= 2 ? s : s.ToLowerInvariant()) switch {
        "never" or "1" => GL.NEVER, "less" or "2" => GL.LESS, "equal" or "3" => GL.EQUAL, "lequal" or "4" => GL.LEQUAL, "greater" or "5" => GL.GREATER,
        "notequal" or "6" => GL.NOTEQUAL, "gequal" or "7" => GL.GEQUAL, "always" or "8" or "0" or "disabled" => GL.ALWAYS, _ => GL.LEQUAL,
    };

    static uint StencilOp(string s) => s.ToLowerInvariant() switch {
        "zero" or "1" => GL.ZERO, "replace" or "2" => GL.REPLACE, "incrsat" or "3" => GL.INCR, "decrsat" or "4" => GL.DECR, "invert" or "5" => GL.INVERT,
        _ => GL.KEEP,
    };

    static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // the GL state last set by ApplyState, so unchanged state is not sent again; Reset() whenever
    // other code may have touched GL (each camera's draw loop starts with it)
    static class Shadow {
        public static int Cull, DepthMask, DepthFunc, Blend, Sc, Dc, Sa, Da, Op, Mask, Offset, Stencil, Program;
        public static float OffF, OffU;
        public static void Reset() { Cull = DepthMask = DepthFunc = Blend = Sc = Dc = Sa = Da = Op = Mask = Offset = Stencil = Program = -1; }
    }

    static void ApplyState(PassState s, NMaterial? m) {
        string cull = s.Cull.Resolve(m);
        int cullV = Is(cull, "off") || cull == "0" ? 0 : Is(cull, "front") || cull == "1" ? 1 : 2;
        if (cullV != Shadow.Cull) {
            if (cullV == 0) GL.glDisable(GL.CULL_FACE);
            else { GL.glEnable(GL.CULL_FACE); GL.glCullFace(cullV == 1 ? GL.FRONT : GL.BACK); }
            if (Shadow.Cull == -1) GL.glFrontFace(GL.CW);
            Shadow.Cull = cullV;
        }
        string zw = s.ZWrite.Resolve(m);
        int zwV = Is(zw, "off") || zw == "0" ? 0 : 1;
        if (zwV != Shadow.DepthMask) { GL.glDepthMask((byte)zwV); Shadow.DepthMask = zwV; }
        int zf = (int)CompareFunc(s.ZTest.Resolve(m));
        if (zf != Shadow.DepthFunc) {
            if (Shadow.DepthFunc == -1) GL.glEnable(GL.DEPTH_TEST);
            GL.glDepthFunc((uint)zf);
            Shadow.DepthFunc = zf;
        }
        int blendOn = 0, sc = 0, dc = 0, sa = 0, da = 0, op = 0;
        if (s.Blend != null) {
            sc = (int)BlendFactor(s.Blend[0].Resolve(m)); dc = (int)BlendFactor(s.Blend[1].Resolve(m));
            sa = s.Blend.Length > 2 ? (int)BlendFactor(s.Blend[2].Resolve(m)) : sc; da = s.Blend.Length > 2 ? (int)BlendFactor(s.Blend[3].Resolve(m)) : dc;
            blendOn = sc == GL.ONE && dc == GL.ZERO && sa == GL.ONE && da == GL.ZERO ? 0 : 1;
            string bo = s.BlendOp.Resolve(m);
            op = (int)(Is(bo, "sub") || bo == "1" ? GL.FUNC_SUBTRACT : Is(bo, "revsub") || bo == "2" ? GL.FUNC_REVERSE_SUBTRACT : Is(bo, "min") || bo == "3" ? GL.MIN : Is(bo, "max") || bo == "4" ? GL.MAX : GL.FUNC_ADD);
        }
        if (blendOn != Shadow.Blend) { if (blendOn == 0) GL.glDisable(GL.BLEND); else GL.glEnable(GL.BLEND); Shadow.Blend = blendOn; }
        if (blendOn == 1 && (sc != Shadow.Sc || dc != Shadow.Dc || sa != Shadow.Sa || da != Shadow.Da)) {
            GL.glBlendFuncSeparate((uint)sc, (uint)dc, (uint)sa, (uint)da);
            Shadow.Sc = sc; Shadow.Dc = dc; Shadow.Sa = sa; Shadow.Da = da;
        }
        if (blendOn == 1 && op != Shadow.Op) { GL.glBlendEquationSeparate((uint)op, (uint)op); Shadow.Op = op; }
        string cm = s.ColorMask;
        int mask = cm == "0" ? 0 : (cm.Contains('R') || cm.Contains('r') ? 1 : 0) | (cm.Contains('G') || cm.Contains('g') ? 2 : 0) | (cm.Contains('B') || cm.Contains('b') ? 4 : 0) | (cm.Contains('A') || cm.Contains('a') ? 8 : 0);
        if (mask != Shadow.Mask) { GL.glColorMask((byte)(mask & 1), (byte)(mask >> 1 & 1), (byte)(mask >> 2 & 1), (byte)(mask >> 3 & 1)); Shadow.Mask = mask; }
        int off = s.HasOffset ? 1 : 0;
        if (off != Shadow.Offset || (off == 1 && (s.OffsetFactor != Shadow.OffF || s.OffsetUnits != Shadow.OffU))) {
            if (off == 1) { GL.glEnable(GL.POLYGON_OFFSET_FILL); GL.glPolygonOffset(s.OffsetFactor, s.OffsetUnits); }
            else GL.glDisable(GL.POLYGON_OFFSET_FILL);
            Shadow.Offset = off; Shadow.OffF = s.OffsetFactor; Shadow.OffU = s.OffsetUnits;
        }
        if (s.StencilEnabled) {
            GL.glEnable(GL.STENCIL_TEST);
            int rf = (int)float.Parse(s.StencilRef.Resolve(m), System.Globalization.CultureInfo.InvariantCulture);
            uint rm = (uint)float.Parse(s.StencilReadMask.Resolve(m), System.Globalization.CultureInfo.InvariantCulture);
            uint wm = (uint)float.Parse(s.StencilWriteMask.Resolve(m), System.Globalization.CultureInfo.InvariantCulture);
            GL.glStencilFunc(CompareFunc(s.StencilComp.Resolve(m)), rf, rm);
            GL.glStencilOp(StencilOp(s.StencilFail.Resolve(m)), StencilOp(s.StencilZFail.Resolve(m)), StencilOp(s.StencilPass.Resolve(m)));
            GL.glStencilMask(wm);
            Shadow.Stencil = 1;
        } else if (Shadow.Stencil != 0) { GL.glDisable(GL.STENCIL_TEST); Shadow.Stencil = 0; }
    }

    static HashSet<string> ActiveKeywords(NMaterial? m) {
        var k = new HashSet<string>(Globals.Keywords);
        if (m != null) k.UnionWith(m.Keywords);
        return k;
    }

    static bool PassRuns(ShaderPass p) {
        if (p.IsGrabPass) return false;
        if (!p.Tags.TryGetValue("LightMode", out var lm)) return true;
        return lm.ToLowerInvariant() switch {
            "always" or "forwardbase" or "vertex" or "vertexlmrgbm" or "vertexlm" => true,
            _ => false,
        };
    }

    /// Binds a pass's program, state, textures and constants. Returns false when it can't draw.
    // the variant chosen for a pass and material, kept while their keywords stay the same
    sealed class Variant { public SubProgram? Vp, Fp; public int Sig = int.MinValue; }
    static readonly Dictionary<(ShaderPass, NMaterial?), Variant> variants = new();

    /// Drops the variants of materials that are gone (destroyed or unloaded): the table held every
    /// material ever drawn, and with them their textures.
    public static int ForgetDeadMaterials() {
        var dead = variants.Keys.Where(k => k.Item2 is { Gone: true }).ToList();
        foreach (var k in dead) variants.Remove(k);
        if (passMat is { Gone: true }) passMat = null;
        return dead.Count;
    }

    static int KeywordSig(NMaterial? m) {
        int h = Globals.Keywords.Count * 7919;
        foreach (var k in Globals.Keywords) h ^= k.GetHashCode();
        if (m != null) { h += m.Keywords.Count * 104729; foreach (var k in m.Keywords) h ^= k.GetHashCode() * 31; }
        return h;
    }

    static unsafe bool BindPass(NMaterial? mat, NShader sh, ShaderPass pass, DrawCtx ctx) {
        int sig = KeywordSig(mat);
        if (!variants.TryGetValue((pass, mat), out var vr)) variants[(pass, mat)] = vr = new Variant();
        if (vr.Sig != sig) {
            var kw = ActiveKeywords(mat);
            vr.Vp = pass.Vp.Count > 0 ? Shaders.Choose(pass.Vp, kw) : null;
            vr.Fp = pass.Fp.Count > 0 ? Shaders.Choose(pass.Fp, kw) : null;
            vr.Sig = sig;
        }
        var vp = vr.Vp;
        var fp = vr.Fp;
        string? alpha = pass.State.AlphaTestFunc;
        if (alpha != null && alpha.ToLowerInvariant() == "always") alpha = null;
        var gp = GetProgram(pass, vp, fp, alpha);
        if (gp.Failed || gp.Id == 0) return false;
        if (Shadow.Program != (int)gp.Id) { GL.glUseProgram(gp.Id); Shadow.Program = (int)gp.Id; }
        ApplyState(pass.State, mat);
        if (gp.AlphaRefLoc >= 0) {
            float aref = float.TryParse(pass.State.AlphaTestRef.Resolve(mat), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ar) ? ar : 0;
            Uniform1f(gp.AlphaRefLoc, aref);
        }
        if (vp != null && gp.VcLoc >= 0) { FillConsts(vp, vbuf, gp.VcCount, ctx); fixed (float* p = vbuf) GL.glUniform4fv(gp.VcLoc, gp.VcCount, p); }
        if (fp != null && gp.PcLoc >= 0) { FillConsts(fp, pbuf, gp.PcCount, ctx); fixed (float* p = pbuf) GL.glUniform4fv(gp.PcLoc, gp.PcCount, p); }
        if (gp.FF != null) FixedFunctionGlsl.SetUniforms(gp, ctx.Mat, M.Mul(ctx.P, M.Mul(ctx.V, ctx.M)));
        foreach (var (loc, unit, name) in gp.Samplers) {
            GL.glActiveTexture(GL.TEXTURE0 + (uint)unit);
            var tex = TextureFor(name, ctx) ?? (name == "_MainTex" || name.Length == 0 ? Textures.White : DefaultTexture(sh, name));
            GL.glBindTexture(GL.TEXTURE_2D, tex.Bind());
        }
        GL.glActiveTexture(GL.TEXTURE0);
        return true;
    }

    static NTexture DefaultTexture(NShader sh, string name) {
        if (sh.Properties.TryGetValue(name, out var p)) {
            string d = p.def.Trim('"', ' ').Split('"')[0];
            if (p.def.Contains("\"black\"")) return Textures.Black;
            if (p.def.Contains("\"bump\"")) return Textures.Bump;
            if (p.def.Contains("\"gray\"") || p.def.Contains("\"grey\"")) return Textures.Grey;
        }
        return Textures.White;
    }

    [System.Runtime.InteropServices.DllImport("libGLESv3.so")] static extern void glUniform1f(int loc, float v);
    static void Uniform1f(int loc, float v) => glUniform1f(loc, v);

    // ---- camera rendering ----
    sealed class DrawItem { public NRenderer R = null!; public NMesh Mesh = null!; public int Sub; public NMaterial Mat = null!; public int Queue; public float Dist; public int Order; public int Seq; public Matrix4x4 Model; }

    static readonly int SkipQueue = int.TryParse(Environment.GetEnvironmentVariable("FF9_SKIP_Q"), out var sq) ? sq : -1;
    static readonly int OnlyQueue = int.TryParse(Environment.GetEnvironmentVariable("FF9_ONLY_Q"), out var oq) ? oq : -1;
    static readonly string? SkipShader = Environment.GetEnvironmentVariable("FF9_SKIP_SHADER");
    static readonly string? SkipCamera = Environment.GetEnvironmentVariable("FF9_SKIP_CAMERA");   // "Name@depth" substring
    public static readonly bool DumpGlsl = Environment.GetEnvironmentVariable("FF9_DUMP_GLSL") != null;
    public static readonly int DrawLogFrame = int.TryParse(Environment.GetEnvironmentVariable("FF9_DRAWLOG"), out var f) ? f : -1;

    static readonly bool NoCull = Environment.GetEnvironmentVariable("FF9_NO_CULL") != null;
    static int culled;
    public static int LastCulled;

    /// All eight corners of the mesh's bounds outside one clip plane (in clip space: x < -w,
    /// x > w, y < -w, y > w, z < -w (GL near), z > w (far)).
    static bool Outside(NMesh mesh, Matrix4x4 model, Matrix4x4 viewProj) {
        var b = mesh.Bounds;
        if (b.size.x == 0 && b.size.y == 0 && b.size.z == 0) return false;
        var mvp = M.Mul(viewProj, model);
        Vector3 mn = b.min, mx = b.max;
        int all = 0x3F;
        for (int i = 0; i < 8; i++) {
            float x = (i & 1) != 0 ? mx.x : mn.x, y = (i & 2) != 0 ? mx.y : mn.y, z = (i & 4) != 0 ? mx.z : mn.z;
            float cx = mvp.m00 * x + mvp.m01 * y + mvp.m02 * z + mvp.m03;
            float cy = mvp.m10 * x + mvp.m11 * y + mvp.m12 * z + mvp.m13;
            float cz = mvp.m20 * x + mvp.m21 * y + mvp.m22 * z + mvp.m23;
            float cw = mvp.m30 * x + mvp.m31 * y + mvp.m32 * z + mvp.m33;
            int o = 0;
            if (cx < -cw) o |= 1; if (cx > cw) o |= 2;
            if (cy < -cw) o |= 4; if (cy > cw) o |= 8;
            if (cz < -cw) o |= 16; if (cz > cw) o |= 32;
            all &= o;
            if (all == 0) return false;
        }
        return all != 0;
    }

    public static long ProfBind, ProfDraw, ProfGather;
    // DrawMeshNow (Material.SetPass + Graphics.DrawMeshNow, GL immediate mode): bind, mesh upload, draw, calls
    public static long ProfNowBind, ProfNowUpload, ProfNowDraw, ProfNowCalls, ProfNowVerts;
    static int epoch;
    static int drawCount;
    public static int LastDrawCount;

    public static void RenderFrame() {
        if (!Device.Ready) return;
        LastDrawCount = drawCount; drawCount = 0;
        LastCulled = culled; culled = 0;
        var cams = Cameras.Live.Where(c => c.Enabled && c.GameObject.ActiveInHierarchy).OrderBy(c => c.Target != null ? 0 : 1).ThenBy(c => c.Depth).ToList();
        RenderTargets.Set(null);
        if (cams.Count == 0) { GL.glClearColor(0, 0, 0, 1); GL.glClear(GL.COLOR_BUFFER_BIT | GL.DEPTH_BUFFER_BIT); }
        foreach (var c in cams) {
            if (SkipCamera != null && $"{c.GameObject.Name}@{c.Depth}" .Contains(SkipCamera)) continue;
            RenderCamera(c, null, null);
        }
        if (traceFx && fxThisFrame && !fxShots) {
            Log.Info("fx: cameras " + string.Join(", ", cams.Select(c => $"'{c.GameObject.Name}' depth {c.Depth} clear {c.ClearFlags} target {(c.Target != null ? "rt" : "screen")} rect {c.PixelRect}")));
            TraceScreen("after cameras");
        }
        Canvases.RenderOverlay();
        if (traceFx && fxThisFrame && !fxShots) TraceScreen("after overlay");
        Gui.OnGUI();
        if (traceFx && fxThisFrame) { if (fxShots) Device.Screenshot(System.IO.Path.Combine(Environment.GetEnvironmentVariable("FF9_WORLDMAP_DIR") ?? ".", "..", $"fx_{Core.Clock.Frame}.png")); else TraceScreen("after OnGUI"); }
        fxThisFrame = false;
        RenderTargets.Set(null);
    }

    static bool fxThisFrame;
    static unsafe void TraceScreen(string when) {
        int w = Device.Width, h = Device.Height;
        var px = new byte[w * h * 4];
        GL.glBindFramebuffer(GL.FRAMEBUFFER, 0);
        GL.glPixelStorei(GL.PACK_ALIGNMENT, 1);
        fixed (byte* p = px) GL.glReadPixels(0, 0, w, h, GL.RGBA, GL.UNSIGNED_BYTE, p);
        long sum = 0; for (int i = 0; i < px.Length; i += 4) sum += px[i] + px[i + 1] + px[i + 2];
        GL.glBindFramebuffer(GL.FRAMEBUFFER, RenderTargets.CurrentFbo);
        Log.Info($"fx: screen {when}={sum / Math.Max(1, px.Length / 4 * 3)}");
    }

    static void SendToCamera(NCamera c, string message) {
        foreach (var comp in c.GameObject.Components.ToArray())
            if (comp is NMonoBehaviour mb && mb.Enabled && mb.Managed != null) {
                var mi = ScriptMethods.Find(mb.Managed.GetType(), message);
                if (mi != null) ScriptMethods.Call(mb, mi);
            }
    }

    static readonly bool traceFx = Environment.GetEnvironmentVariable("FF9_TRACE_FX") != null, fxShots = Environment.GetEnvironmentVariable("FF9_TRACE_FX") == "shots";

    /// FF9_TRACE_FX: average brightness of the swirl's textures (the screen it captured, its own
    /// render textures) and of the camera's picture, to see where black comes from.
    static unsafe void TraceFx(RenderTexture src) {
        static string Mean(NRenderTexture rt) {
            if (!rt.Created) return "not created";
            var px = new byte[rt.Width * rt.Height * 4];
            GL.glBindFramebuffer(GL.FRAMEBUFFER, rt.Fbo);
            GL.glPixelStorei(GL.PACK_ALIGNMENT, 1);
            fixed (byte* p = px) GL.glReadPixels(0, 0, rt.Width, rt.Height, GL.RGBA, GL.UNSIGNED_BYTE, p);
            long sum = 0; for (int i = 0; i < px.Length; i += 4) sum += px[i] + px[i + 1] + px[i + 2];
            return $"{sum / Math.Max(1, px.Length / 4 * 3)}";
        }
        var sb = new StringBuilder($"fx: dt {Core.Clock.Delta:F3}");
        var bs = Core.Player.Behaviours.FirstOrDefault(b => b.Managed?.GetType().Name == "BattleSwirl")?.Managed;
        var rush = bs?.GetType().GetField("_rush", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(bs);
        if (rush != null) sb.Append($" seq {rush.GetType().GetField("_rushSeq", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(rush)}");
        foreach (var b in Core.Player.Behaviours.ToList())
            if (b.Managed is { } w && b.GameObject.ActiveInHierarchy && w.GetType().GetProperty("color") is { } cp && w.GetType().Name is "UISprite" or "UITexture") {
                for (var pt = b.GameObject.Transform; pt != null; pt = pt.Parent)
                    if (pt.GameObject.Name == "Event Fader Panel") { sb.Append($" fader '{b.GameObject.Name}' color {cp.GetValue(w)} enabled {b.Enabled}"); break; }
            }
        var sdT = typeof(FF9StateSystem).Assembly.GetType("Assets.Scripts.Common.SceneDirector");
        const System.Reflection.BindingFlags SF = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        if (sdT != null) sb.Append($" fadeMode {sdT.GetField("fadeMode", SF)?.GetValue(null)} target {sdT.GetField("_targetColor", SF)?.GetValue(null)} prev {sdT.GetField("_prevColor", SF)?.GetValue(null)} cur {sdT.GetField("_curFrame", SF)?.GetValue(null)}/{sdT.GetField("_targetFrame", SF)?.GetValue(null)} abr [{string.Join(";", (sdT.GetField("abrColor", SF)?.GetValue(null) as UnityEngine.Color32[] ?? Array.Empty<UnityEngine.Color32>()).Select(c => c.ToString()))}]");
        if (Textures.OfOrNull(src) is NRenderTexture s0) sb.Append($" camera {s0.Width}x{s0.Height}={Mean(s0)}");
        foreach (var o in Registry.All.ToList())
            if (o is NRenderTexture rt && rt.Name.StartsWith("Rush_RT_")) sb.Append($" {rt.Name}={Mean(rt)}");
        var res = typeof(FF9StateSystem).Assembly.GetType("SFX_Rush")?.GetField("_result", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null) as Texture;
        if (Textures.OfOrNull(res) is { Pixels: { } rp } r) {
            long sum = 0; for (int i = 0; i < rp.Length; i += 4) sum += rp[i] + rp[i + 1] + rp[i + 2];
            sb.Append($" captured {r.Width}x{r.Height}={sum / Math.Max(1, rp.Length / 4 * 3)}");
        }
        {
            int w = Device.Width, h = Device.Height;
            var px = new byte[w * h * 4];
            GL.glBindFramebuffer(GL.FRAMEBUFFER, 0);
            GL.glPixelStorei(GL.PACK_ALIGNMENT, 1);
            fixed (byte* p = px) GL.glReadPixels(0, 0, w, h, GL.RGBA, GL.UNSIGNED_BYTE, p);
            long sum = 0; for (int i = 0; i < px.Length; i += 4) sum += px[i] + px[i + 1] + px[i + 2];
            sb.Append($" screen={sum / Math.Max(1, px.Length / 4 * 3)}");
        }
        GL.glBindFramebuffer(GL.FRAMEBUFFER, RenderTargets.CurrentFbo);
        Log.Info(sb.ToString());
    }

    /// A camera's image effects: its scripts' OnRenderImage(source, destination), in their order.
    static List<(Core.NMonoBehaviour mb, System.Reflection.MethodInfo mi)> ImageEffects(NCamera c) {
        var l = new List<(Core.NMonoBehaviour, System.Reflection.MethodInfo)>();
        foreach (var comp in c.GameObject.Components)
            if (comp is Core.NMonoBehaviour mb && mb.Enabled && mb.Managed != null && OnRenderImageOf(mb.Managed.GetType()) is { } mi) l.Add((mb, mi));
        return l;
    }

    static readonly Dictionary<Type, System.Reflection.MethodInfo?> onRenderImage = new();
    static System.Reflection.MethodInfo? OnRenderImageOf(Type type) {
        if (onRenderImage.TryGetValue(type, out var found)) return found;
        System.Reflection.MethodInfo? r = null;
        for (var t = type; t != null && t != typeof(UnityEngine.MonoBehaviour) && r == null; t = t.BaseType) {
            var mi = t.GetMethod("OnRenderImage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
            if (mi != null && mi.GetParameters().Length == 2) r = mi;
        }
        return onRenderImage[type] = r;
    }

    public static void RenderCamera(NCamera cam, UnityEngine.Shader? replacement, string? replacementTag) {
        // OnRenderImage, as Unity: the camera draws into a temporary texture, which its image
        // effects pass on, the last into the camera's own target (the screen when it has none).
        // The battle swirl (BattleSwirl on SwirlScene's camera) is drawn this way: without it the
        // screen stayed black (GitHub issue 24).
        var fx = replacement == null ? ImageEffects(cam) : null;
        if (fx is { Count: > 0 }) {
            Log.Once("fx" + cam.GameObject.Name, $"camera '{cam.GameObject.Name}': image effects {string.Join(", ", fx.Select(f => f.mi.DeclaringType?.Name))}");
            var camTarget = cam.Target;
            int w = camTarget != null ? Textures.Of(camTarget).Width : Device.Width, h = camTarget != null ? Textures.Of(camTarget).Height : Device.Height;
            var src = TextureIcalls.Temporary(w, h, 24);
            // A camera that doesn't clear the colour draws over what the cameras before it drew,
            // and Unity gives its image effects that picture: the target's contents are copied
            // into the temporary texture first. Without the copy the battle camera (clear Depth,
            // PostBlur) drew over the pooled texture's last frame wherever the battle scene draws
            // nothing (a doorway, above the walls), and moving things left trails there that
            // stayed: the smeared doors and spell effects of GitHub issues 33 and 38.
            if (cam.ClearFlags != CameraClearFlags.Skybox && cam.ClearFlags != CameraClearFlags.SolidColor)
                CopyColour(camTarget != null ? Textures.Of(camTarget) as NRenderTexture : null, (NRenderTexture)Textures.Of(src), w, h);
            cam.Target = src;
            RenderTargets.CameraTarget = Textures.Of(src) as NRenderTexture;
            try { RenderCameraOnce(cam, replacement, replacementTag); }
            finally { cam.Target = camTarget; RenderTargets.CameraTarget = null; }
            var temps = new List<RenderTexture> { src };
            for (int i = 0; i < fx.Count; i++) {
                RenderTexture? dest = i == fx.Count - 1 ? camTarget : TextureIcalls.Temporary(w, h, 0);
                if (dest != null && i < fx.Count - 1) temps.Add(dest);
                try { fx[i].mi.Invoke(fx[i].mb.Managed, new object?[] { src, dest }); }
                catch (System.Reflection.TargetInvocationException e) { Log.Exception(e.InnerException ?? e, $"{fx[i].mi.DeclaringType?.Name}.OnRenderImage"); }
                if (dest != null) src = dest;
            }
            if (traceFx) { if (!fxShots) TraceFx(temps[0]); fxThisFrame = true; }
            foreach (var t in temps) TextureIcalls.ReleaseTemp(t);
            RenderTargets.Set(camTarget != null ? Textures.Of(camTarget) as NRenderTexture : null);
            return;
        }
        RenderCameraOnce(cam, replacement, replacementTag);
    }

    /// Copies a target's colour (null: the screen) into a render texture of the same size.
    static void CopyColour(NRenderTexture? from, NRenderTexture to, int w, int h) {
        if (!Device.Ready) return;
        from?.Create();
        to.Create();
        GL.glDisable(GL.SCISSOR_TEST);
        GL.glColorMask(1, 1, 1, 1);
        GL.glBindFramebuffer(GL.READ_FRAMEBUFFER, from?.Fbo ?? 0);
        GL.glBindFramebuffer(GL.DRAW_FRAMEBUFFER, to.Fbo);
        GL.glBlitFramebuffer(0, 0, w, h, 0, 0, w, h, GL.COLOR_BUFFER_BIT, GL.NEAREST);
        GL.glBindFramebuffer(GL.FRAMEBUFFER, RenderTargets.CurrentFbo);
    }

    // Settings > Picture > Stretch (FF9_STRETCH 1 or 2; GitHub issues 4 and 32). Memoria draws the
    // fields in NarrowMapList (backgrounds 320 pixels wide) and, without widescreen, everything 4:3,
    // by narrowing the camera's viewport (PSXCameraAspect sets Camera.rect), with black at the sides.
    // Stretched, such a camera draws across the whole width: its projection stays the narrow one
    // (Aspect comes from its rect), so the picture is widened, not shown further to the sides. Only
    // a full-height camera at least half the width: a small inset view (a mini map) keeps its rect.
    static readonly bool stretchNarrow = Environment.GetEnvironmentVariable("FF9_STRETCH") is "1" or "2";
    static bool StretchNarrow(NCamera cam) {
        if (!stretchNarrow) return false;
        var r = cam.ViewportRect;
        return r.x > 0.001f && r.width < 0.999f && r.width >= 0.5f && r.y <= 0.01f && r.height >= 0.99f;
    }
    // Stretch everything (FF9_STRETCH 2) on a screen taller than 4:3 (GitHub issue 50: the Retroid
    // Pocket Classic's 1240x1080): the 4:3 picture is then a full-width camera with black above and
    // below, so it is stretched up and down to the screen's height the same way (also when the camera
    // draws into a texture the size of the screen, as the world map's does).
    static readonly bool stretchShort = Environment.GetEnvironmentVariable("FF9_STRETCH") is "2";
    static bool StretchShort(NCamera cam) {
        if (!stretchShort) return false;
        var r = cam.ViewportRect;
        return r.y > 0.001f && r.height < 0.999f && r.height >= 0.5f && r.x <= 0.01f && r.width >= 0.99f;
    }
    // The UI (menus, dialogs, the pause dim, fades) has its own canvas, 1600x1100, which the game
    // fits to the screen's width on screens squarer than that, centred, while its camera covers the
    // whole screen: so with the picture stretched the menus kept bars above and below (GitHub issue
    // 50, 0.1.41; measured 852 of 1080 pixels at 1240x1080). That band is stretched to the full height.
    static Matrix4x4 StretchUi(Matrix4x4 p, int w, int h) {
        float band = w * 1100f / 1600f / h;                 // the canvas's share of the height
        if (band >= 0.999f || band < 0.5f) return p;
        var s = M.Identity;
        s.m11 = 1 / band;                                   // centred: only a scale in clip space
        return M.Mul(s, p);
    }
    static readonly bool traceRects = Environment.GetEnvironmentVariable("FF9_TRACE_CAMRECT") == "1";
    static readonly HashSet<string> tracedRects = new();

    static void RenderCameraOnce(NCamera cam, UnityEngine.Shader? replacement, string? replacementTag) {
        Camera = cam;
        Cameras.Current = cam;
        SendToCamera(cam, "OnPreCull");
        var target = cam.Target != null ? Textures.Of(cam.Target) as NRenderTexture : null;
        RenderTargets.Set(target);
        var pr = cam.PixelRect;
        if (target == null && StretchNarrow(cam)) pr = new Rect(0, pr.y, cam.TargetW, pr.height);
        else if ((target == null || target.Width == Device.Width && target.Height == Device.Height) && StretchShort(cam))
            pr = new Rect(pr.x, 0, pr.width, cam.TargetH);   // the world map draws into a screen-sized texture first
        if (traceRects && tracedRects.Add($"{cam.GameObject.Name} {cam.ViewportRect} {target?.Width}"))
            Log.Info($"camrect: {cam.GameObject.Name} rect {cam.ViewportRect} drawn {pr} of {cam.TargetW}x{cam.TargetH}{(target != null ? " (texture)" : "")}{(cam.Orthographic ? $" ortho {cam.OrthoSize}" : "")} depth {cam.Depth}");
        GL.glViewport((int)pr.x, (int)pr.y, (int)pr.width, (int)pr.height);
        GL.glEnable(GL.SCISSOR_TEST);
        GL.glScissor((int)pr.x, (int)pr.y, (int)pr.width, (int)pr.height);
        GL.glDepthMask(1);
        GL.glColorMask(1, 1, 1, 1);
        GL.glStencilMask(0xFF);
        switch (cam.ClearFlags) {
            case CameraClearFlags.Skybox:
            case CameraClearFlags.SolidColor:
                GL.glClearColor(cam.Background.r, cam.Background.g, cam.Background.b, cam.Background.a);
                GL.glClear(GL.COLOR_BUFFER_BIT | GL.DEPTH_BUFFER_BIT | GL.STENCIL_BUFFER_BIT);
                break;
            case CameraClearFlags.Depth:
                GL.glClear(GL.DEPTH_BUFFER_BIT | GL.STENCIL_BUFFER_BIT);
                break;
        }
        GL.glDisable(GL.SCISSOR_TEST);
        View = cam.View;
        Proj = cam.Projection;
        if (stretchShort && target == null && cam.GameObject.Name == "UI Camera") Proj = StretchUi(Proj, cam.TargetW, cam.TargetH);
        Model = M.Identity;
        CameraPos = cam.GameObject.Transform.Position;
        SendToCamera(cam, "OnPreRender");

        long g0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var viewProj = M.Mul(Proj, View);
        RendererComponents.All.RemoveAll(r => r.Managed == null || Registry.Get(r.Managed) != r);
        // the renderers this camera sees, then their scripts' OnWillRenderObject, as Unity calls it
        // before drawing them: NGUI's UIDrawCall sets a clipped panel's clip range and softness
        // there (without it the clip shaders got zeros and drew nothing)
        var visible = new List<(NRenderer r, NMesh mesh, Matrix4x4 model)>();
        foreach (var r in RendererComponents.All) {
            r.IsVisible = false;
            if (!r.Enabled || r.IsAsset || !r.GameObject.ActiveInHierarchy) continue;
            if ((cam.CullingMask & (1 << r.GameObject.Layer)) == 0) continue;
            var mesh = r.DrawMesh;
            if (mesh == null || mesh.Vertices.Length == 0) continue;
            // off the camera's view: skipped, as Unity culls by bounds (the world map's blocks
            // behind the camera were more than half its draws). Mesh renderers only; skinned and
            // particle renderers move their vertices and are drawn as before.
            var model = r.DrawMatrix;
            if (r is NMeshRenderer && !NoCull && Outside(mesh, model, viewProj)) { culled++; continue; }
            r.IsVisible = true;
            visible.Add((r, mesh, model));
        }
        foreach (var (r, _, _) in visible)
            foreach (var comp in r.GameObject.Components)
                if (comp is NMonoBehaviour mb && mb.Enabled && mb.Managed != null && mb.Methods?.OnWillRenderObject is { } will)
                    ScriptMethods.Call(mb, will);
        NTransform.CacheEpoch = ++epoch == 0 ? ++epoch : epoch;   // no script runs until the sort is done
        var items = new List<DrawItem>();
        int seq = 0;
        foreach (var (r, mesh, model) in visible) {
            var pos = r.GameObject.Transform.Position;
            var vpos = M.MulPoint(View, pos);
            for (int i = 0; i < Math.Max(r.Materials.Length, 1); i++) {
                var mat = i < r.Materials.Length ? Materials.Of(r.Materials[i]) : null;
                if (mat == null) continue;
                int sub = Math.Min(i, mesh.SubMeshes.Count - 1);
                items.Add(new DrawItem { R = r, Mesh = mesh, Sub = sub, Mat = mat, Queue = mat.RenderQueue, Dist = -vpos.z, Order = r.SortingOrder, Seq = seq++, Model = model });
            }
        }
        items.Sort((a, b) => {
            int c = a.Queue.CompareTo(b.Queue);
            if (c != 0) return c;
            if (a.Queue <= 2500) { c = a.Dist.CompareTo(b.Dist); return c != 0 ? c : a.Seq.CompareTo(b.Seq); }
            c = a.Order.CompareTo(b.Order);
            if (c != 0) return c;
            c = b.Dist.CompareTo(a.Dist);
            return c != 0 ? c : a.Seq.CompareTo(b.Seq);
        });
        NTransform.CacheEpoch = 0;
        ProfGather += System.Diagnostics.Stopwatch.GetTimestamp() - g0;
        Shadow.Reset();
        var ctx = new DrawCtx { V = View, P = Proj };
        bool log = DrawLogFrame == Clock.Frame;
        if (log && cam.Depth >= 2) foreach (var kv in Globals.Vectors) Log.Info($"  global {PropIds.Name(kv.Key)} = {kv.Value}");
        if (log && cam.Depth >= 2) foreach (var kv in Globals.Matrices) Log.Info($"  global matrix {PropIds.Name(kv.Key)} = {kv.Value.ToString().Replace("\n", " | ")}");
        if (log) Log.Info($"camera '{cam.GameObject.Name}' tag={cam.GameObject.Tag} enabled={cam.Enabled} depth={cam.Depth} mask={cam.CullingMask:x} ortho={cam.Orthographic} size={cam.OrthoSize} clear={cam.ClearFlags} bg={cam.Background} rect={cam.PixelRect} items={items.Count} near={cam.Near} far={cam.Far} fov={cam.Fov} customProj={cam.CustomProj != null} customView={cam.CustomView != null} pos={cam.GameObject.Transform.Position}");
        foreach (var it in items) {
            if (log) {
                var mvp = M.Mul(Proj, M.Mul(View, it.R.DrawMatrix));
                var v0 = it.Mesh.Vertices[0];
                float cx = mvp.m00 * v0.x + mvp.m01 * v0.y + mvp.m02 * v0.z + mvp.m03, cy = mvp.m10 * v0.x + mvp.m11 * v0.y + mvp.m12 * v0.z + mvp.m13, cw = mvp.m30 * v0.x + mvp.m31 * v0.y + mvp.m32 * v0.z + mvp.m33;
                var tex = it.Mat.Textures.Values.FirstOrDefault();
                string path = it.R.GameObject.Name;
                for (var pt = it.R.GameObject.Transform.Parent; pt != null; pt = pt.Parent) path = pt.GameObject.Name + "/" + path;
                var c0 = it.Mesh.Colors != null && it.Mesh.Colors.Length > 0 ? it.Mesh.Colors[0] : new Color32(255, 255, 255, 255);
                Log.Info($"  draw '{path}' mesh#{it.Mesh.Id} col={c0} q={it.Queue} verts={it.Mesh.Vertices.Length} sub={it.Sub} idx={it.Mesh.SubMeshes[it.Sub].Indices.Length} shader='{it.Mat.Shader?.Name}' tex='{Registry.Get(tex)?.Name}' uv={(it.Mesh.UV != null && it.Mesh.UV.Length > 2 ? it.Mesh.UV[0] + "/" + it.Mesh.UV[2] : "none")} v0={v0} ndc=({cx / cw:F2},{cy / cw:F2}) w={cw:F2}");
            }
            var sh = it.Mat.Shader;
            if (sh == null || !sh.Supported) continue;
            if (SkipShader != null && sh.Name.Contains(SkipShader)) continue;
            if (OnlyQueue >= 0 && it.Queue != OnlyQueue) continue;
            if (it.Queue == SkipQueue) continue;
            if (it.R is NSkinnedMeshRenderer sk) sk.Skin(); else it.Mesh.Upload();
            ctx.Mat = it.Mat;
            ctx.Block = it.R.Block;
            ctx.M = it.R is NSkinnedMeshRenderer ? it.R.DrawMatrix : it.Model;   // skinning may change it
            if (it.R is NSpriteRenderer sr && Sprites.Of(sr.Sprite)?.Texture is { } stex) {
                ctx.Block ??= new NMaterialBlock();
                ctx.Block.Textures[PropIds.Id("_MainTex")] = stex;
            }
            foreach (var pass in sh.Passes) {
                if (!PassRuns(pass)) { if (log) Log.Info($"    pass '{pass.Name}' skipped (LightMode {pass.Tags.GetValueOrDefault("LightMode")})"); continue; }
                LogConsts = log;
                long b0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool bound = BindPass(it.Mat, sh, pass, ctx);
                long b1 = System.Diagnostics.Stopwatch.GetTimestamp();
                ProfBind += b1 - b0;
                LogConsts = false;
                if (!bound) { if (log) Log.Info("    pass bind failed"); continue; }
                it.Mesh.Draw(it.Sub);
                ProfDraw += System.Diagnostics.Stopwatch.GetTimestamp() - b1;
                drawCount++;
                if (log && it.R is NSkinnedMeshRenderer skr && skr.SkinnedPositions is { Length: > 0 } sp)
                    Log.Info($"    skinned v0={sp[0]} v1={sp[Math.Min(100, sp.Length - 1)]} bones={skr.Bones.Length} go={skr.GameObject.Transform.Position} scale={skr.GameObject.Transform.LossyScale}");
                if (log) {
                    var tx = Textures.OfOrNull(it.Mat.Textures.GetValueOrDefault(PropIds.Id("_MainTex")));
                    Log.Info($"    drawn: glerr={GL.glGetError():x} tex gl={tx?.GlTex} {tx?.Width}x{tx?.Height} fmt={tx?.Format} mips={tx?.MipCount} px={(tx?.Pixels != null)} blend={(pass.State.Blend == null ? "off" : string.Join("/", pass.State.Blend.Select(b => b.Resolve(it.Mat))))} cmask={pass.State.ColorMask} zt={pass.State.ZTest.Const} alpha={pass.State.AlphaTestFunc}");
                    if (it.Mat.Vectors.Count > 0 || it.Mat.Textures.Count > 0)
                        Log.Info("    mat#" + it.Mat.Id + " " + string.Join(" ", it.Mat.Vectors.Select(kv => $"{PropIds.Name(kv.Key)}={kv.Value}"))
                            + " tex:" + string.Join(",", it.Mat.Textures.Select(kv => $"{PropIds.Name(kv.Key)}={(kv.Value == null ? "null" : Textures.OfOrNull(kv.Value)?.Name)}"))
                            + " props:" + string.Join(",", sh.Properties.Select(kv => $"{kv.Key}[{kv.Value.type}]={kv.Value.def}")) + $" dflt={it.Mat.DefaultsApplied}" + (Globals.Vectors.TryGetValue(PropIds.Id("_Color"), out var gc) ? $" global_Color={gc}" : ""));
                }
            }
        }
        SendToCamera(cam, "OnPostRender");
        Cameras.Current = null;
        Camera = null;
    }

    // ---- Material.SetPass + GL immediate mode / DrawMeshNow ----
    static NMaterial? passMat;
    static NShader? passShader;
    static ShaderPass? passPass;

    public static bool SetPass(NMaterial m, int pass) {
        var sh = m.Shader;
        if (sh == null || pass < 0 || pass >= sh.Passes.Count) return false;
        passMat = m; passShader = sh; passPass = sh.Passes[pass];
        return true;
    }

    static bool BindCurrentPass(Matrix4x4 model) {
        if (passMat == null || passShader == null || passPass == null || !Device.Ready) return false;
        Shadow.Reset();
        return BindPass(passMat, passShader, passPass, new DrawCtx { Mat = passMat, M = model, V = View, P = Proj });
    }

    public static void DrawMeshNow(NMesh mesh, Matrix4x4 m, int sub) {
        long t0 = Core.Prof.On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (!BindCurrentPass(m)) return;
        long t1 = t0 != 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        mesh.Upload();
        long t2 = t0 != 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (sub < 0) for (int i = 0; i < mesh.SubMeshes.Count; i++) mesh.Draw(i);
        else mesh.Draw(sub);
        if (t0 != 0) {
            ProfNowBind += t1 - t0; ProfNowUpload += t2 - t1; ProfNowDraw += System.Diagnostics.Stopwatch.GetTimestamp() - t2;
            ProfNowCalls++; ProfNowVerts += mesh.Vertices?.Length ?? 0;
        }
    }

    // immediate mode
    static int immMode = -1;
    static readonly List<Vector3> immPos = new();
    static readonly List<Color32> immCol = new();
    static readonly List<Vector2> immUv = new();
    static Color32 curColor = new(255, 255, 255, 255);
    static Vector2 curUv;
    static NMesh? immMesh;

    static class GLImm {
        [Icall("UnityEngine.GL::Begin")] static void Begin(int mode) { immMode = mode; immPos.Clear(); immCol.Clear(); immUv.Clear(); }
        [Icall("UnityEngine.GL::Vertex3")] static void Vertex3(float x, float y, float z) => Vtx(new Vector3(x, y, z));
        [Icall("UnityEngine.GL::INTERNAL_CALL_Vertex")] static void Vertex(ref Vector3 v) => Vtx(v);
        static void Vtx(Vector3 v) { immPos.Add(v); immCol.Add(curColor); immUv.Add(curUv); }
        [Icall("UnityEngine.GL::INTERNAL_CALL_Color")]
        static void Color(ref Color c) => curColor = new Color32((byte)Math.Clamp(c.r * 255, 0, 255), (byte)Math.Clamp(c.g * 255, 0, 255), (byte)Math.Clamp(c.b * 255, 0, 255), (byte)Math.Clamp(c.a * 255, 0, 255));
        [Icall("UnityEngine.GL::INTERNAL_CALL_TexCoord")] static void TexCoord(ref Vector3 v) => curUv = new Vector2(v.x, v.y);
        [Icall("UnityEngine.GL::TexCoord2")] static void TexCoord2(float x, float y) => curUv = new Vector2(x, y);
        [Icall("UnityEngine.GL::TexCoord3")] static void TexCoord3(float x, float y, float z) => curUv = new Vector2(x, y);
        [Icall("UnityEngine.GL::MultiTexCoord2")] static void Multi2(int unit, float x, float y) { if (unit == 0) curUv = new Vector2(x, y); }
        [Icall("UnityEngine.GL::INTERNAL_CALL_MultiTexCoord")] static void Multi(int unit, ref Vector3 v) { if (unit == 0) curUv = new Vector2(v.x, v.y); }

        [Icall("UnityEngine.GL::End")]
        static void End() {
            if (immMode < 0 || immPos.Count == 0) { immMode = -1; return; }
            immMesh ??= new NMesh { Dynamic = true };
            var idx = new List<int>();
            int n = immPos.Count;
            MeshTopology topo = MeshTopology.Triangles;
            switch (immMode) {
                case 4: for (int i = 0; i + 2 < n; i += 3) { idx.Add(i); idx.Add(i + 1); idx.Add(i + 2); } break;          // TRIANGLES
                case 5: for (int i = 0; i + 2 < n; i++) { if (i % 2 == 0) { idx.Add(i); idx.Add(i + 1); idx.Add(i + 2); } else { idx.Add(i + 1); idx.Add(i); idx.Add(i + 2); } } break;
                case 7: for (int i = 0; i + 3 < n; i += 4) { idx.Add(i); idx.Add(i + 1); idx.Add(i + 2); idx.Add(i); idx.Add(i + 2); idx.Add(i + 3); } break;   // QUADS
                case 1: topo = MeshTopology.Lines; for (int i = 0; i + 1 < n; i += 2) { idx.Add(i); idx.Add(i + 1); } break;
                case 2: topo = MeshTopology.LineStrip; for (int i = 0; i < n; i++) idx.Add(i); break;
            }
            immMesh.Vertices = immPos.ToArray(); immMesh.Colors = immCol.ToArray(); immMesh.UV = immUv.ToArray();
            immMesh.SubMeshes = new() { new SubMeshData { Indices = idx.ToArray(), Topology = topo } };
            immMesh.Dirty = true;
            DrawMeshNow(immMesh, Model, 0);
            immMode = -1;
        }

        [Icall("UnityEngine.GL::LoadOrtho")] static void LoadOrtho() { Proj = MathIcallsOrtho(0, 1, 0, 1, -1, 100); View = M.Identity; Model = M.Identity; }
        [Icall("UnityEngine.GL::LoadPixelMatrixArgs")] static void LoadPixel(float l, float r, float b, float t) { Proj = MathIcallsOrtho(l, r, b, t, -1, 100); View = M.Identity; Model = M.Identity; }
        [Icall("UnityEngine.GL::LoadPixelMatrix")] static void LoadPixel0() => LoadPixel(0, RenderTargets.Width, 0, RenderTargets.Height);
        [Icall("UnityEngine.GL::INTERNAL_CALL_Viewport")] static void Viewport(ref Rect r) { if (Device.Ready) GL.glViewport((int)r.x, (int)r.y, (int)r.width, (int)r.height); }
        [Icall("UnityEngine.GL::INTERNAL_CALL_LoadProjectionMatrix")] static void LoadProj(ref Matrix4x4 m) => Proj = m;
        [Icall("UnityEngine.GL::LoadIdentity")] static void LoadIdentity() { View = M.Identity; Model = M.Identity; }
        [Icall("UnityEngine.GL::INTERNAL_CALL_MultMatrix")] static void MultMatrix(ref Matrix4x4 m) => Model = M.Mul(Model, m);
        [Icall("UnityEngine.GL::INTERNAL_get_modelview")] static void GetMV(ref Matrix4x4 m) => m = M.Mul(View, Model);
        [Icall("UnityEngine.GL::INTERNAL_set_modelview")] static void SetMV(ref Matrix4x4 m) { View = M.Identity; Model = m; }
        [Icall("UnityEngine.GL::PushMatrix")] static void Push() => stack.Push((Model, View, Proj));
        [Icall("UnityEngine.GL::PopMatrix")] static void Pop() { if (stack.Count > 0) (Model, View, Proj) = stack.Pop(); }
        [Icall("UnityEngine.GL::INTERNAL_CALL_GetGPUProjectionMatrix")] static Matrix4x4 GpuProj(ref Matrix4x4 p, bool rt) => p;
        [Icall("UnityEngine.GL::set_sRGBWrite")] static void SRGB(bool v) { }
        [Icall("UnityEngine.GL::get_invertCulling")] static bool GetInvert() => false;
        [Icall("UnityEngine.GL::set_invertCulling")] static void SetInvert(bool v) { }
        [Icall("UnityEngine.GL::Flush")] static void Flush() { if (Device.Ready) GL.glFlush(); }
        [Icall("UnityEngine.GL::IssuePluginEvent")] static void PluginEvent(int id) { }
        [Icall("UnityEngine.GL::IssuePluginEventInternal")] static void PluginEventI(IntPtr cb, int id) { }

        [Icall("UnityEngine.GL::INTERNAL_CALL_Internal_Clear")]
        static void Clear(bool depth, bool color, ref Color bg, float d) {
            if (!Device.Ready) return;
            uint mask = 0;
            if (color) { GL.glColorMask(1, 1, 1, 1); GL.glClearColor(bg.r, bg.g, bg.b, bg.a); mask |= GL.COLOR_BUFFER_BIT; }
            if (depth) { GL.glDepthMask(1); GL.glClearDepthf(d); mask |= GL.DEPTH_BUFFER_BIT | GL.STENCIL_BUFFER_BIT; }
            GL.glClear(mask);
        }

        [Icall("UnityEngine.GL::ClearWithSkybox")]
        static void ClearWithSkybox(bool depth, Camera cam) {
            var c = Registry.Of<NCamera>(cam);
            var bg = c.Background;
            Clear(depth, true, ref bg, 1);
        }

        static Matrix4x4 MathIcallsOrtho(float l, float r, float b, float t, float n, float f) => Icalls.MathIcallsPublic.Ortho(l, r, b, t, n, f);
    }

    /// Mirror of UnityEngine's internal InternalDrawTextureArguments (same field order).
    public struct DrawTexArgs {
        public Rect screenRect; public Texture texture; public Rect sourceRect;
        public int leftBorder, rightBorder, topBorder, bottomBorder;
        public Color32 color; public Material mat;
    }

    // ---- Graphics ----
    static class GraphicsIcalls {
        [Icall("UnityEngine.Graphics::INTERNAL_CALL_Internal_DrawMeshNow2")]
        static void DrawMeshNow2(Mesh mesh, ref Matrix4x4 m, int sub) { var nm = Meshes.Of(mesh); if (nm != null) DrawMeshNow(nm, m, sub); }

        [Icall("UnityEngine.Graphics::INTERNAL_CALL_Internal_DrawMeshNow1")]
        static void DrawMeshNow1(Mesh mesh, ref Vector3 pos, ref Quaternion rot, int sub) { var nm = Meshes.Of(mesh); if (nm != null) DrawMeshNow(nm, M.TRS(pos, rot, new Vector3(1, 1, 1)), sub); }

        [Icall("UnityEngine.Graphics::DrawProceduralIndirect")] static void DrawProcIndirect(MeshTopology t, ComputeBuffer b, int o) => Log.Once("DrawProceduralIndirect");

        static NMaterial? blitMat;

        static void BlitQuad(Texture? src, NMaterial mat, int pass) {
            var saved = (Model, View, Proj);
            Proj = Icalls.MathIcallsPublic.Ortho(0, 1, 0, 1, -1, 100); View = M.Identity; Model = M.Identity;
            if (src != null) { mat.Textures[PropIds.Id("_MainTex")] = src; mat.TexST[PropIds.Id("_MainTex")] = new Vector4(1, 1, 0, 0); }
            var q = Quad();
            var sh = mat.Shader;
            if (sh != null) {
                for (int i = 0; i < sh.Passes.Count; i++) {
                    if (pass >= 0 && i != pass) continue;
                    SetPass(mat, i);
                    DrawMeshNow(q, M.Identity, 0);
                }
            }
            (Model, View, Proj) = saved;
        }

        static NMesh? quad;
        static NMesh Quad() {
            if (quad != null) return quad;
            quad = new NMesh {
                Vertices = new[] { new Vector3(0, 0, 0.1f), new Vector3(1, 0, 0.1f), new Vector3(1, 1, 0.1f), new Vector3(0, 1, 0.1f) },
                UV = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) },
            };
            quad.SubMeshes = new() { new SubMeshData { Indices = new[] { 0, 2, 1, 0, 3, 2 } } };
            return quad;
        }

        static NMaterial BlitMaterial() {
            if (blitMat != null) return blitMat;
            var sh = new NShader { Source = "Shader \"Hidden/FF9Blit\" { SubShader { Pass { ZTest Always Cull Off ZWrite Off SetTexture [_MainTex] { combine texture } } } }" };
            Shaders.Parse(sh);
            var sm = (UnityEngine.Shader)RuntimeHelpers.GetUninitializedObject(typeof(UnityEngine.Shader));
            Registry.Add(new Assets.NAsset { Data = sh, IsAsset = true }, sm);
            blitMat = new NMaterial { ShaderObj = sm };
            return blitMat;
        }

        [Icall("UnityEngine.Graphics::Blit")]
        static void Blit(Texture src, RenderTexture? dest) {
            if (!Device.Ready) return;
            RenderTargets.Set(dest == null ? null : (NRenderTexture)Textures.Of(dest));
            BlitQuad(src, BlitMaterial(), 0);
        }

        [Icall("UnityEngine.Graphics::Internal_BlitMaterial")]
        static void BlitMaterial(Texture src, RenderTexture? dest, Material mat, int pass, bool setRT) {
            if (!Device.Ready) return;
            if (setRT) RenderTargets.Set(dest == null ? null : (NRenderTexture)Textures.Of(dest));
            var m = Materials.Of(mat);
            if (m != null) BlitQuad(src, m, pass);
        }

        [Icall("UnityEngine.Graphics::Internal_BlitMultiTap")]
        static void BlitMultiTap(Texture src, RenderTexture dest, Material mat, Vector2[] offsets) => BlitMaterial(src, dest, mat, -1, true);

        [Icall("UnityEngine.Graphics::Internal_SetNullRT")] static void SetNullRT() => RenderTargets.Set(null);

        [Icall("UnityEngine.Graphics::Internal_SetRTSimple")]
        static unsafe void SetRT(ref RenderBuffer color, ref RenderBuffer depth, int mip, int face) {
            int id;
            fixed (RenderBuffer* p = &color) id = *(int*)p;
            RenderTargets.Set(Registry.ById(id) as NRenderTexture);
        }

        [Icall("UnityEngine.Graphics::ClearRandomWriteTargets")] static void ClearRW() { }
        [Icall("UnityEngine.Graphics::Internal_SetRandomWriteTargetRT")] static void SetRW(int i, RenderTexture rt) { }

        static NMesh? drawTexQuad;
        static readonly bool traceDrawTex = Environment.GetEnvironmentVariable("FF9_TRACE_DRAWTEX") != null;

        [Icall("UnityEngine.Graphics::DrawTexture")]
        static unsafe void DrawTexture(ref DrawTexArgs a) {
            if (!Device.Ready) return;
            var tex = Textures.OfOrNull(a.texture);
            var mat = Materials.Of(a.mat) ?? BlitMaterial();
            // PSX/BattleMap_TexAnim (the models' texture animation, e.g. blinking) only copies the
            // texture; its vertex program pushes z=0 geometry past the far plane here
            if (mat.Shader?.Name == "PSX/BattleMap_TexAnim") mat = BlitMaterial();
            if (traceDrawTex) Log.Once("drawtex" + a.screenRect + (tex?.Id ?? 0), $"DrawTexture tex=#{tex?.Id} '{tex?.Name}' {tex?.Width}x{tex?.Height} px={tex?.Pixels != null} gl={tex?.GlTex} screen={a.screenRect} src={a.sourceRect} color={a.color} target={RenderTargets.Active?.Name} fbo={RenderTargets.CurrentFbo} shader={mat.Shader?.Name} proj={Proj.ToString().Replace("\n", " | ")}");
            var saved = (Model, View, Proj);
            // screen rect in GUI pixel coordinates (top-left origin) -> current projection is set by the caller (GL.LoadPixelMatrix)
            var r = a.screenRect; var s = a.sourceRect;
            // one dynamic quad, rewritten per call (a new mesh per call leaked its GL buffers)
            var q = drawTexQuad ??= new NMesh { Dynamic = true, SubMeshes = new() { new SubMeshData { Indices = new[] { 0, 1, 2, 0, 2, 3 } } } };
            q.Vertices = new[] { new Vector3(r.x, r.y, 0), new Vector3(r.x + r.width, r.y, 0), new Vector3(r.x + r.width, r.y + r.height, 0), new Vector3(r.x, r.y + r.height, 0) };
            q.UV = new[] { new Vector2(s.x, s.y + s.height), new Vector2(s.x + s.width, s.y + s.height), new Vector2(s.x + s.width, s.y), new Vector2(s.x, s.y) };
            var c32 = a.color;
            q.Colors = new[] { c32, c32, c32, c32 };
            q.Dirty = true;
            if (tex != null) mat.Textures[PropIds.Id("_MainTex")] = a.texture;
            var sh = mat.Shader;
            if (sh != null) for (int i = 0; i < sh.Passes.Count; i++) { SetPass(mat, i); DrawMeshNow(q, Model, 0); }
            (Model, View, Proj) = saved;
        }
    }
}
