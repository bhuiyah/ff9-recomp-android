// Transform hierarchy: local TRS, parent/children, world matrices.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace FF9Engine.Core;

public class NTransform : NComponent {
    public Vector3 LocalPosition;
    public Quaternion LocalRotation = new(0, 0, 0, 1);
    public Vector3 LocalScale = new(1, 1, 1);
    public NTransform? Parent;
    public readonly List<NTransform> Children = new();
    public bool HasChanged = true;

    public NTransform Root { get { var t = this; while (t.Parent != null) t = t.Parent; return t; } }

    public Matrix4x4 LocalMatrix => M.TRS(LocalPosition, LocalRotation, LocalScale);

    // While a camera gathers its renderers no script runs, so world matrices are kept for that
    // window (Pipeline sets CacheEpoch, 0 outside it) and a child reuses its parent's
    public static int CacheEpoch;
    int cachedEpoch;
    Matrix4x4 cachedL2W;

    public Matrix4x4 LocalToWorld {
        get {
            if (CacheEpoch != 0) {
                if (cachedEpoch == CacheEpoch) return cachedL2W;
                var w = Parent == null ? LocalMatrix : M.Mul(Parent.LocalToWorld, LocalMatrix);
                cachedL2W = w;
                cachedEpoch = CacheEpoch;
                return w;
            }
            var m = LocalMatrix;
            for (var p = Parent; p != null; p = p.Parent) m = M.Mul(p.LocalMatrix, m);
            return m;
        }
    }

    public Matrix4x4 WorldToLocal => M.Inverse(LocalToWorld);

    public Quaternion Rotation {
        get {
            var q = LocalRotation;
            for (var p = Parent; p != null; p = p.Parent) q = M.Mul(p.LocalRotation, q);
            return q;
        }
        set => LocalRotation = Parent == null ? value : M.Mul(M.Inverse(Parent.Rotation), value);
    }

    public Vector3 Position {
        get => Parent == null ? LocalPosition : M.MulPoint(Parent.LocalToWorld, LocalPosition);
        set => LocalPosition = Parent == null ? value : M.MulPoint(Parent.WorldToLocal, value);
    }

    public Vector3 LossyScale {
        get {
            var s = LocalScale;
            for (var p = Parent; p != null; p = p.Parent)
                s = new Vector3(s.x * p.LocalScale.x, s.y * p.LocalScale.y, s.z * p.LocalScale.z);
            return s;
        }
    }

    public void SetParent(NTransform? parent, bool worldPositionStays) {
        if (parent == Parent) return;
        Icalls.GameObjectIcalls.HierarchyVersion++;
        for (var p = parent; p != null; p = p.Parent) if (p == this) return;
        Vector3 wp = default; Quaternion wr = default; Vector3 ws = default;
        if (worldPositionStays) { wp = Position; wr = Rotation; ws = LossyScale; }
        Parent?.Children.Remove(this);
        Parent = parent;
        parent?.Children.Add(this);
        if (worldPositionStays) {
            Rotation = wr;
            Position = wp;
            var ps = parent?.LossyScale ?? new Vector3(1, 1, 1);
            LocalScale = new Vector3(Div(ws.x, ps.x), Div(ws.y, ps.y), Div(ws.z, ps.z));
        }
        MarkChanged();
    }

    static float Div(float a, float b) => b == 0 ? a : a / b;

    public void MarkChanged() {
        HasChanged = true;
        foreach (var c in Children) c.MarkChanged();
    }

    public bool IsChildOf(NTransform t) {
        for (var p = this; p != null; p = p.Parent) if (p == t) return true;
        return false;
    }

    public NTransform? Find(string path) {
        NTransform? cur = this;
        foreach (var part in path.Split('/')) {
            if (cur == null) return null;
            if (part == "..") { cur = cur.Parent; continue; }
            if (part.Length == 0) continue;
            NTransform? found = null;
            foreach (var c in cur.Children) if (c.GameObject.Name == part) { found = c; break; }
            cur = found;
        }
        return cur;
    }
}

public sealed class NRectTransform : NTransform {
    public Vector2 AnchorMin, AnchorMax, AnchoredPosition, SizeDelta, Pivot = new(0.5f, 0.5f);
}

/// Unity-convention math (column vectors, left-handed), independent of UnityEngine's own
/// methods so the engine never calls back into internal calls.
public static class M {
    public static Quaternion Mul(Quaternion a, Quaternion b) => new(
        a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
        a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
        a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x,
        a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

    public static Quaternion Inverse(Quaternion q) {
        float n = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        if (n == 0) return new Quaternion(0, 0, 0, 1);
        return new Quaternion(-q.x / n, -q.y / n, -q.z / n, q.w / n);
    }

    public static Quaternion Normalize(Quaternion q) {
        float n = MathF.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return n < 1e-6f ? new Quaternion(0, 0, 0, 1) : new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n);
    }

    public static Vector3 Rotate(Quaternion q, Vector3 v) {
        float x = q.x * 2, y = q.y * 2, z = q.z * 2;
        float xx = q.x * x, yy = q.y * y, zz = q.z * z, xy = q.x * y, xz = q.x * z, yz = q.y * z;
        float wx = q.w * x, wy = q.w * y, wz = q.w * z;
        return new Vector3(
            (1 - (yy + zz)) * v.x + (xy - wz) * v.y + (xz + wy) * v.z,
            (xy + wz) * v.x + (1 - (xx + zz)) * v.y + (yz - wx) * v.z,
            (xz - wy) * v.x + (yz + wx) * v.y + (1 - (xx + yy)) * v.z);
    }

    const float Deg = MathF.PI / 180f;

    /// Unity's Euler order: Z, then X, then Y (q = qy * qx * qz), degrees.
    public static Quaternion Euler(Vector3 e) {
        Quaternion qx = Axis(new Vector3(1, 0, 0), e.x * Deg), qy = Axis(new Vector3(0, 1, 0), e.y * Deg),
                   qz = Axis(new Vector3(0, 0, 1), e.z * Deg);
        return Mul(Mul(qy, qx), qz);
    }

    public static Quaternion Axis(Vector3 axis, float rad) {
        float l = MathF.Sqrt(axis.x * axis.x + axis.y * axis.y + axis.z * axis.z);
        if (l < 1e-9f) return new Quaternion(0, 0, 0, 1);
        float s = MathF.Sin(rad / 2) / l;
        return new Quaternion(axis.x * s, axis.y * s, axis.z * s, MathF.Cos(rad / 2));
    }

    public static Vector3 ToEuler(Quaternion q) {
        // inverse of Euler(): rotation matrix element extraction for ZXY order
        q = Normalize(q);
        float sinx = 2 * (q.w * q.x - q.y * q.z);
        float x = MathF.Abs(sinx) >= 1 ? MathF.CopySign(MathF.PI / 2, sinx) : MathF.Asin(sinx);
        float y = MathF.Atan2(2 * (q.w * q.y + q.x * q.z), 1 - 2 * (q.x * q.x + q.y * q.y));
        float z = MathF.Atan2(2 * (q.w * q.z + q.x * q.y), 1 - 2 * (q.x * q.x + q.z * q.z));
        return new Vector3(Wrap(x / Deg), Wrap(y / Deg), Wrap(z / Deg));
    }

    static float Wrap(float a) { a %= 360; return a < 0 ? a + 360 : a; }

    public static Matrix4x4 TRS(Vector3 t, Quaternion q, Vector3 s) {
        var m = new Matrix4x4();
        float x = q.x * 2, y = q.y * 2, z = q.z * 2;
        float xx = q.x * x, yy = q.y * y, zz = q.z * z, xy = q.x * y, xz = q.x * z, yz = q.y * z;
        float wx = q.w * x, wy = q.w * y, wz = q.w * z;
        m.m00 = (1 - (yy + zz)) * s.x; m.m01 = (xy - wz) * s.y; m.m02 = (xz + wy) * s.z; m.m03 = t.x;
        m.m10 = (xy + wz) * s.x; m.m11 = (1 - (xx + zz)) * s.y; m.m12 = (yz - wx) * s.z; m.m13 = t.y;
        m.m20 = (xz - wy) * s.x; m.m21 = (yz + wx) * s.y; m.m22 = (1 - (xx + yy)) * s.z; m.m23 = t.z;
        m.m33 = 1;
        return m;
    }

    public static Matrix4x4 Identity { get { var m = new Matrix4x4(); m.m00 = m.m11 = m.m22 = m.m33 = 1; return m; } }

    public static unsafe Matrix4x4 Mul(Matrix4x4 a, Matrix4x4 b) {
        var r = new Matrix4x4();
        float* pa = (float*)&a, pb = (float*)&b, pr = (float*)&r;   // column-major: [col*4+row]
        for (int c = 0; c < 4; c++)
            for (int row = 0; row < 4; row++) {
                float s = 0;
                for (int k = 0; k < 4; k++) s += pa[k * 4 + row] * pb[c * 4 + k];
                pr[c * 4 + row] = s;
            }
        return r;
    }

    public static Vector3 MulPoint(Matrix4x4 m, Vector3 v) => new(
        m.m00 * v.x + m.m01 * v.y + m.m02 * v.z + m.m03,
        m.m10 * v.x + m.m11 * v.y + m.m12 * v.z + m.m13,
        m.m20 * v.x + m.m21 * v.y + m.m22 * v.z + m.m23);

    public static Vector3 MulVector(Matrix4x4 m, Vector3 v) => new(
        m.m00 * v.x + m.m01 * v.y + m.m02 * v.z,
        m.m10 * v.x + m.m11 * v.y + m.m12 * v.z,
        m.m20 * v.x + m.m21 * v.y + m.m22 * v.z);

    public static unsafe Matrix4x4 Inverse(Matrix4x4 mat) {
        float* m = (float*)&mat;
        var inv = new float[16];
        inv[0] = m[5] * m[10] * m[15] - m[5] * m[11] * m[14] - m[9] * m[6] * m[15] + m[9] * m[7] * m[14] + m[13] * m[6] * m[11] - m[13] * m[7] * m[10];
        inv[4] = -m[4] * m[10] * m[15] + m[4] * m[11] * m[14] + m[8] * m[6] * m[15] - m[8] * m[7] * m[14] - m[12] * m[6] * m[11] + m[12] * m[7] * m[10];
        inv[8] = m[4] * m[9] * m[15] - m[4] * m[11] * m[13] - m[8] * m[5] * m[15] + m[8] * m[7] * m[13] + m[12] * m[5] * m[11] - m[12] * m[7] * m[9];
        inv[12] = -m[4] * m[9] * m[14] + m[4] * m[10] * m[13] + m[8] * m[5] * m[14] - m[8] * m[6] * m[13] - m[12] * m[5] * m[10] + m[12] * m[6] * m[9];
        inv[1] = -m[1] * m[10] * m[15] + m[1] * m[11] * m[14] + m[9] * m[2] * m[15] - m[9] * m[3] * m[14] - m[13] * m[2] * m[11] + m[13] * m[3] * m[10];
        inv[5] = m[0] * m[10] * m[15] - m[0] * m[11] * m[14] - m[8] * m[2] * m[15] + m[8] * m[3] * m[14] + m[12] * m[2] * m[11] - m[12] * m[3] * m[10];
        inv[9] = -m[0] * m[9] * m[15] + m[0] * m[11] * m[13] + m[8] * m[1] * m[15] - m[8] * m[3] * m[13] - m[12] * m[1] * m[11] + m[12] * m[3] * m[9];
        inv[13] = m[0] * m[9] * m[14] - m[0] * m[10] * m[13] - m[8] * m[1] * m[14] + m[8] * m[2] * m[13] + m[12] * m[1] * m[10] - m[12] * m[2] * m[9];
        inv[2] = m[1] * m[6] * m[15] - m[1] * m[7] * m[14] - m[5] * m[2] * m[15] + m[5] * m[3] * m[14] + m[13] * m[2] * m[7] - m[13] * m[3] * m[6];
        inv[6] = -m[0] * m[6] * m[15] + m[0] * m[7] * m[14] + m[4] * m[2] * m[15] - m[4] * m[3] * m[14] - m[12] * m[2] * m[7] + m[12] * m[3] * m[6];
        inv[10] = m[0] * m[5] * m[15] - m[0] * m[7] * m[13] - m[4] * m[1] * m[15] + m[4] * m[3] * m[13] + m[12] * m[1] * m[7] - m[12] * m[3] * m[5];
        inv[14] = -m[0] * m[5] * m[14] + m[0] * m[6] * m[13] + m[4] * m[1] * m[14] - m[4] * m[2] * m[13] - m[12] * m[1] * m[6] + m[12] * m[2] * m[5];
        inv[3] = -m[1] * m[6] * m[11] + m[1] * m[7] * m[10] + m[5] * m[2] * m[11] - m[5] * m[3] * m[10] - m[9] * m[2] * m[7] + m[9] * m[3] * m[6];
        inv[7] = m[0] * m[6] * m[11] - m[0] * m[7] * m[10] - m[4] * m[2] * m[11] + m[4] * m[3] * m[10] + m[8] * m[2] * m[7] - m[8] * m[3] * m[6];
        inv[11] = -m[0] * m[5] * m[11] + m[0] * m[7] * m[9] + m[4] * m[1] * m[11] - m[4] * m[3] * m[9] - m[8] * m[1] * m[7] + m[8] * m[3] * m[5];
        inv[15] = m[0] * m[5] * m[10] - m[0] * m[6] * m[9] - m[4] * m[1] * m[10] + m[4] * m[2] * m[9] + m[8] * m[1] * m[6] - m[8] * m[2] * m[5];
        float det = m[0] * inv[0] + m[1] * inv[4] + m[2] * inv[8] + m[3] * inv[12];
        var r = new Matrix4x4();
        if (det == 0) return r;
        float* pr = (float*)&r;
        for (int i = 0; i < 16; i++) pr[i] = inv[i] / det;
        return r;
    }
}
