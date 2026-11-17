// Builds GameObject hierarchies from serialized files: whole scenes, or one prefab hierarchy
// from an asset file. Objects are created first, then filled (they reference each other).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using FF9Engine.Core;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FF9Engine.Assets;

public static class SceneLoader {
    public static List<NMonoBehaviour> Load(int index, NScene scene) {
        var f = index == 0 ? AssetDB.Main : AssetDB.File("level" + (index - 1)) ?? throw new System.IO.FileNotFoundException("level" + (index - 1));
        var b = new SceneBuilder(f, scene, false);
        b.BuildAll();
        if (System.Environment.GetEnvironmentVariable("FF9_TRACE_SCENE") != null)
            Log.Info($"scene file {f.Name}: {f.ObjectList.Count} objects, {f.ObjectList.Count(o => o.ClassId == ClassIds.GameObject)} game objects, {b.Behaviours.Count} behaviours");
        return b.Behaviours;
    }
}

public sealed class SceneBuilder : IPPtrResolver {
    readonly SerializedFile file;
    readonly NScene? scene;
    readonly bool asset;
    readonly Dictionary<long, NObject> made = new();
    readonly List<long> order = new();
    public readonly List<NMonoBehaviour> Behaviours = new();

    /// Component data readers for built-in classes (Camera, SpriteRenderer, ...), by class ID.
    public static readonly Dictionary<int, Action<NComponent, BinReader, SceneBuilder>> ComponentReaders = new();

    public SceneBuilder(SerializedFile file, NScene? scene, bool asset) {
        this.file = file;
        this.scene = scene;
        this.asset = asset;
    }

    public SerializedFile File => file;

    public void BuildAll() {
        foreach (var o in file.ObjectList)
            if (o.ClassId == ClassIds.GameObject) MakeGameObject(o.PathId);
        Fill();
    }

    /// A prefab: the whole hierarchy containing this GameObject or Transform.
    public void BuildHierarchyOf(long pathId) {
        var info = file.Objects[pathId];
        long go = info.ClassId == ClassIds.GameObject ? pathId : file.Reader(info).PPtr().PathId;
        // walk up to the root through the transforms' fathers
        long root = go;
        for (int guard = 0; guard < 256; guard++) {
            long t = TransformOf(root);
            if (t == 0) break;
            var r = file.Reader(t);
            r.PPtr(); r.Quat(); r.V3(); r.V3();
            int nc = r.I32(); r.Skip(nc * 12L);
            var (ffi, fpi) = r.PPtr();
            if (fpi == 0 || ffi != 0) break;
            root = file.Reader(fpi).PPtr().PathId;
        }
        MakeTree(root);
        Fill();
    }

    long TransformOf(long go) {
        var r = file.Reader(go);
        int n = r.I32();
        for (int i = 0; i < n; i++) {
            int cid = r.I32();
            var (_, pid) = r.PPtr();
            if (cid == ClassIds.Transform || cid == ClassIds.RectTransform) return pid;
        }
        return 0;
    }

    void MakeTree(long go) {
        MakeGameObject(go);
        long t = TransformOf(go);
        if (t == 0) return;
        var r = file.Reader(t);
        r.PPtr(); r.Quat(); r.V3(); r.V3();
        int nc = r.I32();
        var kids = new List<long>();
        for (int i = 0; i < nc; i++) kids.Add(r.PPtr().PathId);
        foreach (var k in kids) MakeTree(file.Reader(k).PPtr().PathId);
    }

    void MakeGameObject(long pid) {
        if (made.ContainsKey(pid) || AssetDB.Loaded(file, pid) != null) return;
        var r = file.Reader(pid);
        int n = r.I32();
        var comps = new List<(int cid, long pid)>();
        for (int i = 0; i < n; i++) { int cid = r.I32(); var (fi, cp) = r.PPtr(); if (fi == 0) comps.Add((cid, cp)); }
        var go = new NGameObject { Layer = (int)r.U32(), Name = r.String(), IsAsset = asset, Scene = scene, ClassId = 1 };
        ushort tag = r.U16();
        go.Tag = AssetDB.Tags.GetValueOrDefault(tag, "Untagged");
        go.ActiveSelf = r.U8() != 0;
        var mgo = (GameObject)RuntimeHelpers.GetUninitializedObject(typeof(GameObject));
        Registry.Add(go, mgo);
        made[pid] = go;
        order.Add(pid);
        AssetDB.Remember(file, pid, go);
        foreach (var (cid, cp) in comps) {
            var c = MakeComponent(cid, cp, go);
            if (c != null) {
                c.Name = go.Name;   // Unity: a component's name is its GameObject's
                if (c is NTransform) go.Components.Insert(0, c); else go.Components.Add(c);
            }
        }
        if (go.Components.Count == 0 || go.Components[0] is not NTransform) {
            Log.Warn($"GameObject '{go.Name}' has no Transform");
            var t = Registry.Add(new NTransform { GameObject = go, ClassId = 4, Name = go.Name }, (UObject)RuntimeHelpers.GetUninitializedObject(typeof(Transform)));
            go.Components.Insert(0, t);
        }
    }

    NComponent? MakeComponent(int cid, long pid, NGameObject go) {
        if (!file.Objects.TryGetValue(pid, out var info)) return null;
        NComponent c;
        UObject m;
        if (cid == ClassIds.MonoBehaviour) {
            var r = file.Reader(info);
            r.PPtr(); r.U8(); r.Align4();
            var (sfi, spi) = r.PPtr();
            var (type, orderNo) = AssetDB.Script(file, sfi, spi);
            if (type == null) return null;
            m = (UObject)RuntimeHelpers.GetUninitializedObject(type);
            c = new NMonoBehaviour { ExecutionOrder = orderNo };
        } else if (cid == ClassIds.Transform || cid == ClassIds.RectTransform) {
            c = cid == ClassIds.RectTransform ? new NRectTransform() : new NTransform();
            m = (UObject)RuntimeHelpers.GetUninitializedObject(cid == ClassIds.RectTransform ? typeof(RectTransform) : typeof(Transform));
        } else {
            var t = ClassIds.ManagedType(cid);
            if (t == null) { Log.Once("comp" + cid, $"component class {ClassIds.Name(cid)} ({cid}) skipped"); return null; }
            c = Components.New(cid);
            m = (UObject)RuntimeHelpers.GetUninitializedObject(t);
        }
        c.GameObject = go;
        c.IsAsset = asset;
        c.ClassId = cid;
        Registry.Add(c, m);
        made[pid] = c;
        order.Add(pid);
        AssetDB.Remember(file, pid, c);
        return c;
    }

    void Fill() {
        // transforms first (MonoBehaviour fields may read the hierarchy only later, but parents
        // must be linked before anything else uses them)
        foreach (var pid in order)
            if (made[pid] is NTransform t) FillTransform(t, file.Reader(pid));
        foreach (var pid in order) {
            var o = made[pid];
            switch (o) {
                case NMonoBehaviour mb: FillMono(mb, file.Reader(pid), file.TreeOf(file.Objects[pid])); break;
                case NTransform: break;
                case NComponent c: FillComponent(c, file.Reader(pid)); break;
            }
        }
        // a text field loaded from another release's subclass (AssetDB.Script: UIInputPSVitaExtension
        // as UIInput) whose label didn't come through: its own UILabel, as NGUI needs one
        foreach (var pid in order)
            if (made[pid] is NMonoBehaviour { Managed: { } inp } mb && inp.GetType().Name == "UIInput") GiveLabel(mb, inp);
        // a prefab asset's scripts never run: only its instances do
        if (!asset)
            foreach (var pid in order)
                if (made[pid] is NMonoBehaviour mb) Player.AddBehaviour(mb);
    }

    static void GiveLabel(NMonoBehaviour mb, object input) {
        var lf = input.GetType().GetField("label");
        if (lf == null || lf.GetValue(input) != null) return;
        foreach (var ch in mb.GameObject.Transform.Children)
            foreach (var cc in ch.GameObject.Components)
                if (cc.Managed is { } lbl && lbl.GetType().Name == "UILabel") {
                    lf.SetValue(input, lbl);
                    Log.Info($"text field {mb.GameObject.Name}: label set to {ch.GameObject.Name}");
                    return;
                }
    }

    void FillTransform(NTransform t, BinReader r) {
        r.PPtr();
        t.LocalRotation = r.Quat();
        t.LocalPosition = r.V3();
        t.LocalScale = r.V3();
        int n = r.I32();
        var kids = new List<long>();
        for (int i = 0; i < n; i++) kids.Add(r.PPtr().PathId);
        var (ffi, fpi) = r.PPtr();
        if (t is NRectTransform rt) {
            rt.AnchorMin = r.V2(); rt.AnchorMax = r.V2(); rt.AnchoredPosition = r.V2(); rt.SizeDelta = r.V2(); rt.Pivot = r.V2();
        }
        foreach (var k in kids)
            if (made.TryGetValue(k, out var ko) && ko is NTransform kt) { kt.Parent = t; t.Children.Add(kt); }
    }

    void FillMono(NMonoBehaviour mb, BinReader r, TypeTree? tree) {
        r.PPtr();
        mb.Enabled = r.U8() != 0; r.Align4();
        r.PPtr();
        string serializedName = r.String();
        mb.Name = mb.GameObject?.Name ?? serializedName;
        Scripts.RunConstructor(mb.Managed!);
        Scripts.ReadFields(mb.Managed!, r, file, this, tree);
        if (!asset) Behaviours.Add(mb);
    }

    static readonly string? dumpTree = Environment.GetEnvironmentVariable("FF9_DUMP_TREE");
    static readonly HashSet<int> dumped = new();

    void FillComponent(NComponent c, BinReader r) {
        long start = r.Pos;
        if (dumpTree == c.ClassId.ToString() && dumped.Add(c.ClassId) && file.HasTypeTrees) {
            foreach (var o in file.ObjectList)
                if (o.ClassId == c.ClassId && file.TreeOf(o) is { } tree) {
                    foreach (var n in tree.Root.Children) {
                        Log.Info($"  {ClassIds.Name(c.ClassId)} tree: {n.Type} {n.Name} size={n.ByteSize}{(n.Align ? " align" : "")}");
                        foreach (var g in n.Children) Log.Info($"      {g.Type} {g.Name} size={g.ByteSize}{(g.Align ? " align" : "")}");
                    }
                    break;
                }
        }
        if (ComponentReaders.TryGetValue(c.ClassId, out var reader)) {
            try { reader(c, r, this); } catch (Exception e) { Log.Exception(e, $"reading {ClassIds.Name(c.ClassId)}"); }
            return;
        }
        r.PPtr();
        var t = c.Managed!.GetType();
        if (typeof(Behaviour).IsAssignableFrom(t) || typeof(Renderer).IsAssignableFrom(t)) c.Enabled = r.U8() != 0;
        c.Raw = (file, r.Pos);
    }

    public UObject? Resolve(SerializedFile f, int fileId, long pathId, Type expected) {
        if (fileId == 0 && f == file && made.TryGetValue(pathId, out var o)) return o.Managed;
        var tf = AssetDB.External(f, fileId);
        if (tf == null) return null;
        if (tf == file && !asset && AssetDB.Loaded(tf, pathId) is { } lo) return lo.Managed;
        return AssetDB.Load(tf, pathId);
    }
}
