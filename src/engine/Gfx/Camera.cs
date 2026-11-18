// Cameras: component data, matrices, and the Camera internal calls.
using System;
using System.Collections.Generic;
using System.Linq;
using FF9Engine.Assets;
using FF9Engine.Core;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FF9Engine.Gfx;

public sealed class NCamera : NComponent {
    public CameraClearFlags ClearFlags = CameraClearFlags.Skybox;
    public Color Background = new(0.19f, 0.3f, 0.47f, 0);
    public Rect ViewportRect = new(0, 0, 1, 1);
    public float Near = 0.3f, Far = 1000, Fov = 60;
    public bool Orthographic;
    public float OrthoSize = 5;
    public float Depth;
    public int CullingMask = -1, EventMask = -1;
    public RenderTexture? Target;
    public int TargetDisplay;
    public RenderingPath Path = RenderingPath.Forward;
    public bool Hdr;
    public DepthTextureMode DepthTextureMode;
    public TransparencySortMode SortMode;
    public Matrix4x4? CustomView, CustomProj;

    public int TargetW => Target != null ? Textures.Of(Target).Width : Device.Width;
    public int TargetH => Target != null ? Textures.Of(Target).Height : Device.Height;
    public Rect PixelRect => new(ViewportRect.x * TargetW, ViewportRect.y * TargetH, ViewportRect.width * TargetW, ViewportRect.height * TargetH);
    public float Aspect { get { var r = PixelRect; return r.height > 0 ? r.width / r.height : 1; } }

    /// World to camera: Unity's camera looks down +Z; camera space is right-handed (-Z forward).
    public Matrix4x4 View {
        get {
            if (CustomView is { } v) return v;
            var t = GameObject.Transform;
            var w = M.TRS(t.Position, t.Rotation, new Vector3(1, 1, 1));
            var flip = M.Identity; flip.m22 = -1;
            return M.Mul(flip, M.Inverse(w));
        }
    }

    public Matrix4x4 Projection {
        get {
            if (CustomProj is { } p) return p;
            if (Orthographic) return Icalls.MathIcallsPublic.Ortho(-OrthoSize * Aspect, OrthoSize * Aspect, -OrthoSize, OrthoSize, Near, Far);
            return Icalls.MathIcallsPublic.Perspective(Fov, Aspect, Near, Far);
        }
    }
}

public static class Cameras {
    public static readonly List<NCamera> All = new();
    public static NCamera? Current;

    public static void Register() {
        Components.Factories[ClassIds.Camera] = () => { var c = new NCamera(); All.Add(c); return c; };
        SceneBuilder.ComponentReaders[ClassIds.Camera] = (c, r, b) => {
            var cam = (NCamera)c;
            r.PPtr();
            cam.Enabled = r.Bool(); r.Align4();
            cam.ClearFlags = (CameraClearFlags)r.U32();
            cam.Background = r.Color();
            cam.ViewportRect = r.Rect();
            cam.Near = r.F32(); cam.Far = r.F32(); cam.Fov = r.F32();
            cam.Orthographic = r.Bool(); r.Align4();
            cam.OrthoSize = r.F32();
            cam.Depth = r.F32();
            cam.CullingMask = (int)r.U32();
            cam.Path = (RenderingPath)r.I32();
            var (tfi, tpi) = r.PPtr();
            if (tpi != 0) cam.Target = b.Resolve(b.File, tfi, tpi, typeof(RenderTexture)) as RenderTexture;
            cam.TargetDisplay = r.I32();
            cam.Hdr = r.Bool();
        };
        Cloner.RecordCopiers[typeof(NCamera)] = c => {
            var n = (NCamera)Cloner.CloneRecord(c);
            n.CustomView = null; n.CustomProj = null;
            All.Add(n);
            return n;
        };
    }

    /// Cameras in the scene (a prefab asset's camera never renders)
    public static IEnumerable<NCamera> Live => All.Where(c => c.Managed != null && Registry.Get(c.Managed) == c && !c.GameObject.IsAsset);

    static NCamera C(Camera c) => Registry.Of<NCamera>(c);

    [Icall("UnityEngine.Camera::get_fieldOfView")] static float GetFov(Camera c) => C(c).Fov;
    [Icall("UnityEngine.Camera::set_fieldOfView")] static void SetFov(Camera c, float v) => C(c).Fov = v;
    [Icall("UnityEngine.Camera::get_nearClipPlane")] static float GetNear(Camera c) => C(c).Near;
    [Icall("UnityEngine.Camera::set_nearClipPlane")] static void SetNear(Camera c, float v) => C(c).Near = v;
    [Icall("UnityEngine.Camera::get_farClipPlane")] static float GetFar(Camera c) => C(c).Far;
    [Icall("UnityEngine.Camera::set_farClipPlane")] static void SetFar(Camera c, float v) => C(c).Far = v;
    [Icall("UnityEngine.Camera::get_renderingPath")] static RenderingPath GetPath(Camera c) => C(c).Path;
    [Icall("UnityEngine.Camera::set_renderingPath")] static void SetPath(Camera c, RenderingPath v) => C(c).Path = v;
    [Icall("UnityEngine.Camera::get_actualRenderingPath")] static RenderingPath ActualPath(Camera c) => RenderingPath.Forward;
    [Icall("UnityEngine.Camera::get_hdr")] static bool GetHdr(Camera c) => false;
    [Icall("UnityEngine.Camera::set_hdr")] static void SetHdr(Camera c, bool v) => C(c).Hdr = v;
    [Icall("UnityEngine.Camera::get_orthographicSize")] static float GetOS(Camera c) => C(c).OrthoSize;
    [Icall("UnityEngine.Camera::set_orthographicSize")] static void SetOS(Camera c, float v) => C(c).OrthoSize = v;
    [Icall("UnityEngine.Camera::get_orthographic")] static bool GetOrtho(Camera c) => C(c).Orthographic;
    [Icall("UnityEngine.Camera::set_orthographic")] static void SetOrtho(Camera c, bool v) => C(c).Orthographic = v;
    [Icall("UnityEngine.Camera::get_opaqueSortMode")] static UnityEngine.Rendering.OpaqueSortMode GetOSM(Camera c) => 0;
    [Icall("UnityEngine.Camera::get_transparencySortMode")] static TransparencySortMode GetTSM(Camera c) => C(c).SortMode;
    [Icall("UnityEngine.Camera::set_transparencySortMode")] static void SetTSM(Camera c, TransparencySortMode v) => C(c).SortMode = v;
    [Icall("UnityEngine.Camera::get_depth")] static float GetDepth(Camera c) => C(c).Depth;
    [Icall("UnityEngine.Camera::set_depth")] static void SetDepth(Camera c, float v) => C(c).Depth = v;
    [Icall("UnityEngine.Camera::get_aspect")] static float GetAspect(Camera c) => C(c).Aspect;
    [Icall("UnityEngine.Camera::set_aspect")] static void SetAspect(Camera c, float v) { }
    [Icall("UnityEngine.Camera::ResetAspect")] static void ResetAspect(Camera c) { }
    [Icall("UnityEngine.Camera::get_cullingMask")] static int GetMask(Camera c) => C(c).CullingMask;
    [Icall("UnityEngine.Camera::set_cullingMask")] static void SetMask(Camera c, int v) => C(c).CullingMask = v;
    [Icall("UnityEngine.Camera::get_eventMask")] static int GetEMask(Camera c) => C(c).EventMask;
    [Icall("UnityEngine.Camera::set_eventMask")] static void SetEMask(Camera c, int v) => C(c).EventMask = v;
    [Icall("UnityEngine.Camera::INTERNAL_get_backgroundColor")] static void GetBg(Camera c, ref Color v) => v = C(c).Background;
    [Icall("UnityEngine.Camera::INTERNAL_set_backgroundColor")] static void SetBg(Camera c, ref Color v) => C(c).Background = v;
    [Icall("UnityEngine.Camera::INTERNAL_get_rect")] static void GetRect(Camera c, ref Rect v) => v = C(c).ViewportRect;
    [Icall("UnityEngine.Camera::INTERNAL_set_rect")] static void SetRect(Camera c, ref Rect v) => C(c).ViewportRect = v;
    [Icall("UnityEngine.Camera::INTERNAL_get_pixelRect")] static void GetPRect(Camera c, ref Rect v) => v = C(c).PixelRect;
    [Icall("UnityEngine.Camera::INTERNAL_set_pixelRect")]
    static void SetPRect(Camera c, ref Rect v) { var x = C(c); x.ViewportRect = new Rect(v.x / x.TargetW, v.y / x.TargetH, v.width / x.TargetW, v.height / x.TargetH); }
    [Icall("UnityEngine.Camera::get_targetTexture")] static RenderTexture? GetTarget(Camera c) => C(c).Target;
    [Icall("UnityEngine.Camera::set_targetTexture")] static void SetTarget(Camera c, RenderTexture? v) => C(c).Target = v;
    [Icall("UnityEngine.Camera::get_pixelWidth")] static int PW(Camera c) => (int)C(c).PixelRect.width;
    [Icall("UnityEngine.Camera::get_pixelHeight")] static int PH(Camera c) => (int)C(c).PixelRect.height;
    [Icall("UnityEngine.Camera::INTERNAL_get_cameraToWorldMatrix")] static void C2W(Camera c, ref Matrix4x4 v) => v = M.Inverse(C(c).View);
    [Icall("UnityEngine.Camera::INTERNAL_get_worldToCameraMatrix")] static void W2C(Camera c, ref Matrix4x4 v) => v = C(c).View;
    [Icall("UnityEngine.Camera::INTERNAL_set_worldToCameraMatrix")] static void SetW2C(Camera c, ref Matrix4x4 v) => C(c).CustomView = v;
    [Icall("UnityEngine.Camera::INTERNAL_CALL_ResetWorldToCameraMatrix")] static void ResetW2C(Camera c) => C(c).CustomView = null;
    [Icall("UnityEngine.Camera::INTERNAL_get_projectionMatrix")] static void GetProj(Camera c, ref Matrix4x4 v) => v = C(c).Projection;
    [Icall("UnityEngine.Camera::INTERNAL_set_projectionMatrix")] static void SetProj(Camera c, ref Matrix4x4 v) => C(c).CustomProj = v;
    [Icall("UnityEngine.Camera::INTERNAL_CALL_ResetProjectionMatrix")] static void ResetProj(Camera c) => C(c).CustomProj = null;
    [Icall("UnityEngine.Camera::get_clearFlags")] static CameraClearFlags GetClear(Camera c) => C(c).ClearFlags;
    [Icall("UnityEngine.Camera::set_clearFlags")] static void SetClear(Camera c, CameraClearFlags v) => C(c).ClearFlags = v;
    [Icall("UnityEngine.Camera::get_stereoEnabled")] static bool Stereo(Camera c) => false;
    [Icall("UnityEngine.Camera::get_stereoSeparation")] static float StereoSep(Camera c) => 0.022f;
    [Icall("UnityEngine.Camera::get_stereoConvergence")] static float StereoConv(Camera c) => 10;
    [Icall("UnityEngine.Camera::get_stereoMirrorMode")] static bool StereoMirror(Camera c) => false;
    [Icall("UnityEngine.Camera::get_cameraType")] static CameraType CamType(Camera c) => CameraType.Game;
    [Icall("UnityEngine.Camera::get_targetDisplay")] static int TargetDisplay(Camera c) => C(c).TargetDisplay;
    [Icall("UnityEngine.Camera::get_useOcclusionCulling")] static bool Occl(Camera c) => false;
    [Icall("UnityEngine.Camera::set_useOcclusionCulling")] static void SetOccl(Camera c, bool v) { }
    [Icall("UnityEngine.Camera::get_layerCullSpherical")] static bool Spherical(Camera c) => false;
    [Icall("UnityEngine.Camera::get_depthTextureMode")] static DepthTextureMode GetDTM(Camera c) => C(c).DepthTextureMode;
    [Icall("UnityEngine.Camera::set_depthTextureMode")] static void SetDTM(Camera c, DepthTextureMode v) => C(c).DepthTextureMode = v;
    [Icall("UnityEngine.Camera::get_clearStencilAfterLightingPass")] static bool ClearStencil(Camera c) => false;
    [Icall("UnityEngine.Camera::get_commandBufferCount")] static int CmdBufs(Camera c) => 0;
    [Icall("UnityEngine.Camera::get_layerCullDistances")] static float[] CullDist(Camera c) => new float[32];
    [Icall("UnityEngine.Camera::set_layerCullDistances")] static void SetCullDist(Camera c, float[] v) { }

    [Icall("UnityEngine.Camera::CopyFrom")]
    static void CopyFrom(Camera c, Camera other) {
        var d = C(c); var s = C(other);
        d.ClearFlags = s.ClearFlags; d.Background = s.Background; d.ViewportRect = s.ViewportRect; d.Near = s.Near; d.Far = s.Far;
        d.Fov = s.Fov; d.Orthographic = s.Orthographic; d.OrthoSize = s.OrthoSize; d.Depth = s.Depth; d.CullingMask = s.CullingMask;
        d.Target = s.Target; d.CustomView = s.CustomView; d.CustomProj = s.CustomProj;
        d.GameObject.Transform.Position = s.GameObject.Transform.Position;
        d.GameObject.Transform.Rotation = s.GameObject.Transform.Rotation;
    }

    [Icall("UnityEngine.Camera::get_main")]
    static Camera? Main() { long pt = Core.Prof.T0(); var r = MainImpl(); Core.Prof.Call(6, pt); return r; }
    static Camera? MainImpl() => Live.Where(c => c.Enabled && c.GameObject.ActiveInHierarchy && c.GameObject.Tag == "MainCamera").OrderBy(c => c.Depth).FirstOrDefault()?.Managed as Camera;

    [Icall("UnityEngine.Camera::get_current")] static Camera? CurrentCam() => Current?.Managed as Camera;

    static IEnumerable<NCamera> Enabled => Live.Where(c => c.Enabled && c.GameObject.ActiveInHierarchy);
    [Icall("UnityEngine.Camera::get_allCamerasCount")] static int Count() => Enabled.Count();
    [Icall("UnityEngine.Camera::GetAllCameras")]
    static int GetAll(Camera[] arr) {
        int i = 0;
        foreach (var c in Enabled) { if (i >= arr.Length) break; arr[i++] = (Camera)c.Managed!; }
        return i;
    }

    [Icall("UnityEngine.Camera::Render")] static void Render(Camera c) => Pipeline.RenderCamera(C(c), null, null);
    [Icall("UnityEngine.Camera::RenderWithShader")] static void RenderWithShader(Camera c, UnityEngine.Shader s, string tag) => Pipeline.RenderCamera(C(c), s, tag);

    // ---- projections ----
    static Vector4 Clip(NCamera c, Vector3 p) {
        var vp = M.Mul(c.Projection, c.View);
        return new Vector4(
            vp.m00 * p.x + vp.m01 * p.y + vp.m02 * p.z + vp.m03,
            vp.m10 * p.x + vp.m11 * p.y + vp.m12 * p.z + vp.m13,
            vp.m20 * p.x + vp.m21 * p.y + vp.m22 * p.z + vp.m23,
            vp.m30 * p.x + vp.m31 * p.y + vp.m32 * p.z + vp.m33);
    }

    static Vector3 WorldToViewport(NCamera c, Vector3 p) {
        var clip = Clip(c, p);
        var vpos = M.MulPoint(c.View, p);
        float w = clip.w == 0 ? 1e-6f : clip.w;
        return new Vector3((clip.x / w + 1) / 2, (clip.y / w + 1) / 2, -vpos.z);
    }

    static Vector3 ViewportToWorld(NCamera c, Vector3 v) {
        var inv = M.Inverse(M.Mul(c.Projection, c.View));
        float x = v.x * 2 - 1, y = v.y * 2 - 1;
        // depth v.z is distance along the view direction: find NDC z for it
        var pv = c.Projection;
        float zv = -v.z;
        float zc = pv.m22 * zv + pv.m23, wc = pv.m32 * zv + pv.m33;
        float zn = wc == 0 ? 0 : zc / wc;
        float X = inv.m00 * x + inv.m01 * y + inv.m02 * zn + inv.m03;
        float Y = inv.m10 * x + inv.m11 * y + inv.m12 * zn + inv.m13;
        float Z = inv.m20 * x + inv.m21 * y + inv.m22 * zn + inv.m23;
        float W = inv.m30 * x + inv.m31 * y + inv.m32 * zn + inv.m33;
        return W == 0 ? new Vector3(X, Y, Z) : new Vector3(X / W, Y / W, Z / W);
    }

    [Icall("UnityEngine.Camera::INTERNAL_CALL_WorldToViewportPoint")]
    static Vector3 W2V(Camera c, ref Vector3 p) => WorldToViewport(C(c), p);

    [Icall("UnityEngine.Camera::INTERNAL_CALL_WorldToScreenPoint")]
    static Vector3 W2S(Camera c, ref Vector3 p) {
        var x = C(c); var v = WorldToViewport(x, p); var r = x.PixelRect;
        return new Vector3(r.x + v.x * r.width, r.y + v.y * r.height, v.z);
    }

    [Icall("UnityEngine.Camera::INTERNAL_CALL_ViewportToWorldPoint")]
    static Vector3 V2W(Camera c, ref Vector3 v) => ViewportToWorld(C(c), v);

    [Icall("UnityEngine.Camera::INTERNAL_CALL_ScreenToWorldPoint")]
    static Vector3 S2W(Camera c, ref Vector3 s) {
        var x = C(c); var r = x.PixelRect;
        return ViewportToWorld(x, new Vector3((s.x - r.x) / r.width, (s.y - r.y) / r.height, s.z));
    }

    [Icall("UnityEngine.Camera::INTERNAL_CALL_ScreenToViewportPoint")]
    static Vector3 S2V(Camera c, ref Vector3 s) { var r = C(c).PixelRect; return new Vector3((s.x - r.x) / r.width, (s.y - r.y) / r.height, s.z); }

    [Icall("UnityEngine.Camera::INTERNAL_CALL_ViewportToScreenPoint")]
    static Vector3 V2S(Camera c, ref Vector3 v) { var r = C(c).PixelRect; return new Vector3(r.x + v.x * r.width, r.y + v.y * r.height, v.z); }

    [Icall("UnityEngine.Camera::INTERNAL_CALL_ScreenPointToRay")]
    static Ray ScreenRay(Camera c, ref Vector3 s) {
        var x = C(c); var r = x.PixelRect;
        var vp = new Vector3((s.x - r.x) / r.width, (s.y - r.y) / r.height, x.Near);
        var a = ViewportToWorld(x, vp);
        var b = ViewportToWorld(x, new Vector3(vp.x, vp.y, x.Near + 1));
        var d = new Vector3(b.x - a.x, b.y - a.y, b.z - a.z);
        return new Ray(a, d);
    }

    [Icall("UnityEngine.Camera::INTERNAL_CALL_ViewportPointToRay")]
    static Ray ViewportRay(Camera c, ref Vector3 v) {
        var x = C(c);
        var a = ViewportToWorld(x, new Vector3(v.x, v.y, x.Near));
        var b = ViewportToWorld(x, new Vector3(v.x, v.y, x.Near + 1));
        return new Ray(a, new Vector3(b.x - a.x, b.y - a.y, b.z - a.z));
    }
}
