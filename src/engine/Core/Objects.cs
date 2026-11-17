// Native-side objects. Every UnityEngine.Object the game sees has a native record here; the
// managed object's m_CachedPtr and m_InstanceID both hold the record's id (Unity's "== null"
// checks m_CachedPtr, so destroying an object zeroes it).
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FF9Engine.Core;

[StructLayout(LayoutKind.Sequential)]
sealed class ObjectLayout {
    public int InstanceID;
    public IntPtr CachedPtr;
}

public class NObject {
    public int Id;
    public UObject? Managed;
    public string Name = "";
    public HideFlags HideFlags;
    public bool DontDestroy;
    public bool IsAsset;          // loaded from an asset file (not a scene object)
    public bool Gone;             // destroyed or unloaded (caches keyed by the record drop it)
    public int ClassId;
    public override string ToString() => $"{GetType().Name}#{Id} '{Name}'";
    /// Frees GL objects when the object is destroyed.
    public virtual void FreeGpu() { }
    /// Drops GL handles copied by a memberwise clone (the copy makes its own).
    public virtual void ForgetGpu() { }
}

public sealed class NGameObject : NObject {
    public readonly List<NComponent> Components = new();
    public int Layer;
    public string Tag = "Untagged";
    public bool ActiveSelf = true;
    public bool IsStatic;
    public NScene? Scene;

    public NTransform Transform => (NTransform)Components[0];

    public bool ActiveInHierarchy {
        get {
            for (var t = Transform; t != null; t = t.Parent)
                if (!t.GameObject.ActiveSelf) return false;
            return true;
        }
    }
}

public class NComponent : NObject {
    public NGameObject GameObject = null!;
    public bool Enabled = true;   // Behaviour.enabled / Renderer.enabled / Collider.enabled
    public (Assets.SerializedFile File, long Pos) Raw;   // serialized data not decoded yet
}

/// Factory for built-in component records (subclasses register here by class ID).
public static class Components {
    public static readonly Dictionary<int, Func<NComponent>> Factories = new();
    public static NComponent New(int classId) => Factories.TryGetValue(classId, out var f) ? f() : new NComponent();
}

public sealed class NMonoBehaviour : NComponent {
    public bool Awoken, Started, EnabledCalled;
    public int ExecutionOrder;
    public MonoBehaviour MB => (MonoBehaviour)Managed!;
    public Scripting.ScriptMethods Methods = null!;
}

public static class Registry {
    static NObject?[] objs = new NObject?[1 << 16];
    static int next = 2;            // ids: even, nonzero

    public static IEnumerable<NObject> All {
        get { for (int i = 2; i < next; i += 2) if (objs[i >> 1] is { } o) yield return o; }
    }

    /// "live/ever" object counts, for memory reports
    public static string Count {
        get { int live = 0; for (int i = 2; i < next; i += 2) if (objs[i >> 1] != null) live++; return $"{live}/{next >> 1}"; }
    }

    public static T Add<T>(T n, UObject? managed) where T : NObject {
        n.Id = next;
        next += 2;
        int slot = n.Id >> 1;
        if (slot >= objs.Length) Array.Resize(ref objs, objs.Length * 2);
        objs[slot] = n;
        if (managed is not null) Bind(n, managed);
        return n;
    }

    public static void Bind(NObject n, UObject managed) {
        var l = Unsafe.As<ObjectLayout>(managed);
        l.InstanceID = n.Id;
        l.CachedPtr = n.Id;
        n.Managed = managed;
    }

    public static NObject? ById(int id) => id > 0 && (id >> 1) < objs.Length ? objs[id >> 1] : null;

    public static NObject? Get(UObject? m) {
        if (m is null) return null;
        int id = (int)Unsafe.As<ObjectLayout>(m).CachedPtr;
        return id == 0 ? null : objs[id >> 1];
    }

    /// The native record behind a live object; NullReferenceException (as Unity) for dead ones.
    public static T Of<T>(UObject? m) where T : NObject =>
        Get(m) as T ?? throw new NullReferenceException($"{typeof(T).Name}: object is null or destroyed");

    /// UnloadUnusedAssets (Core/Unloader.cs): an object out of the table while the GC decides
    /// whether anything still reaches its managed object, then back in or gone.
    internal static void Hide(NObject n) => objs[n.Id >> 1] = null;
    internal static void Unhide(NObject n) => objs[n.Id >> 1] = n;

    internal static void MarkGone(NObject n) {
        n.Gone = true;
        if (n is Assets.NAsset { Data: NObject d }) d.Gone = true;
    }

    public static void Remove(NObject n) {
        if (n.Managed is { } m) Unsafe.As<ObjectLayout>(m).CachedPtr = IntPtr.Zero;
        objs[n.Id >> 1] = null;
        MarkGone(n);
        if (Gfx.Device.Ready) n.FreeGpu();
    }

    /// The managed object for a record, created on demand for built-in classes.
    public static UObject? Managed(NObject? n) => n?.Managed;

    public static T? ManagedAs<T>(NObject? n) where T : UObject => n?.Managed as T;
}
