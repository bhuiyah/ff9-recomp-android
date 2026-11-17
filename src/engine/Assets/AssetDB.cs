// The build's serialized files: scenes (mainData = scene 0, levelN = scene N+1), sharedassets,
// resources.assets, the built-in resource files, and objects loaded from them.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using FF9Engine.Core;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FF9Engine.Assets;

public sealed class NAsset : NObject {
    public SerializedFile? File;
    public long PathId;
    public object? Data;          // decoded content, filled by the class's reader

    // a copy of a file's texture, mesh or material (Cloner: Renderer.material, Instantiate) is an
    // NAsset too: its content's GL objects go with it
    public override void FreeGpu() { if (Data is NObject d) d.FreeGpu(); }
}

public static class AssetDB {
    public static string DataDir = "";
    public static string[] SceneNames = Array.Empty<string>();
    public static readonly Dictionary<int, string> Tags = new() {
        [0] = "Untagged", [1] = "Respawn", [2] = "Finish", [3] = "EditorOnly", [5] = "MainCamera", [6] = "Player",
        [7] = "GameController",
    };
    public static readonly List<string> Layers = new();
    public static SerializedFile Main = null!;
    static readonly Dictionary<string, SerializedFile?> files = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<(SerializedFile, long), NObject> loaded = new();

    /// Class-specific asset readers (Texture2D, Material, ...): fill an NAsset's Data/managed state.
    public static readonly Dictionary<int, Action<NAsset, BinReader>> Readers = new();

    public static void Init() {
        DataDir = Path.Combine(Directory.GetCurrentDirectory(), "x64", "FF9_Data");
        Main = File("mainData") ?? throw new FileNotFoundException("mainData");
        foreach (var o in Main.ObjectList) {
            if (o.ClassId == ClassIds.BuildSettings) {
                var r = Main.Reader(o);
                int n = r.I32();
                SceneNames = new string[n];
                for (int i = 0; i < n; i++) SceneNames[i] = r.String();
            } else if (o.ClassId == ClassIds.TagManager) {
                var r = Main.Reader(o);
                int n = r.I32();
                for (int i = 0; i < n; i++) Tags[20000 + i] = r.String();
                int nl = r.I32();
                for (int i = 0; i < nl; i++) Layers.Add(r.String());
            }
        }
        Log.Info($"build: {SceneNames.Length} scenes, {Tags.Count} tags, {Layers.Count} layers");
    }

    public static SerializedFile? File(string name) {
        if (files.TryGetValue(name, out var f)) return f;
        string p = name.ToLowerInvariant() switch {
            "library/unity default resources" => Path.Combine(DataDir, "Resources", "unity default resources"),
            "resources/unity_builtin_extra" => Path.Combine(DataDir, "Resources", "unity_builtin_extra"),
            _ => Path.Combine(DataDir, name),
        };
        f = System.IO.File.Exists(p) ? SerializedFile.Open(p) : null;
        if (f == null) Log.Warn($"asset file missing: {name}");
        files[name] = f;
        return f;
    }

    public static void Register(SerializedFile f, string name) => files[name] = f;

    public static SerializedFile? External(SerializedFile from, int fileId) {
        if (fileId == 0) return from;
        if (fileId - 1 >= from.Externals.Count) return null;
        string e = from.Externals[fileId - 1];
        if (files.TryGetValue(e, out var f)) return f;
        // bundle-internal references ("archive:/CAB-.../CAB-...") and plain names
        string key = e.StartsWith("archive:/") ? Path.GetFileName(e) : e;
        return files.TryGetValue(key, out f) ? f : File(e);
    }

    /// The live object made from (file, pathId); null once it was destroyed (a scene loaded again
    /// makes its objects anew).
    public static NObject? Loaded(SerializedFile f, long pathId) =>
        loaded.TryGetValue((f, pathId), out var n) && Core.Registry.ById(n.Id) == n ? n : null;

    public static void Remember(SerializedFile f, long pathId, NObject o) => loaded[(f, pathId)] = o;

    /// The object at (file, pathId), created on first use. Scene objects are created by the
    /// scene loader; here: assets (prefab hierarchies, scriptable objects, textures, ...).
    public static UObject? Load(SerializedFile f, long pathId) {
        if (loaded.TryGetValue((f, pathId), out var n)) return Registry.Get(n.Managed) != null ? n.Managed : null;
        if (!f.Objects.TryGetValue(pathId, out var info)) {
            Log.Warn($"{f.Name}: no object {pathId}");
            return null;
        }
        switch (info.ClassId) {
            case ClassIds.GameObject:
            case ClassIds.Transform:
            case ClassIds.RectTransform:
                new SceneBuilder(f, null, true).BuildHierarchyOf(pathId);
                return loaded.TryGetValue((f, pathId), out n) ? n.Managed : null;
            case ClassIds.MonoBehaviour: {
                var r = f.Reader(info);
                var (gfi, gpi) = r.PPtr();
                if (gfi != 0 || gpi != 0) {   // a prefab's component
                    new SceneBuilder(f, null, true).BuildHierarchyOf(gpi);
                    return loaded.TryGetValue((f, pathId), out n) ? n.Managed : null;
                }
                return LoadScriptableObject(f, info);
            }
            default:
                if (info.ClassId != ClassIds.AssetBundle && (ClassIds.ManagedType(info.ClassId) is { } t) && typeof(Component).IsAssignableFrom(t)) {
                    var r = f.Reader(info);
                    var (_, gpi) = r.PPtr();
                    new SceneBuilder(f, null, true).BuildHierarchyOf(gpi);
                    return loaded.TryGetValue((f, pathId), out n) ? n.Managed : null;
                }
                return LoadAsset(f, info)?.Managed;
        }
    }

    public static NAsset? LoadAsset(SerializedFile f, ObjectInfo info) {
        var t = ClassIds.ManagedType(info.ClassId);
        if (t == null) {
            Log.Once("class" + info.ClassId, $"asset class {ClassIds.Name(info.ClassId)} ({info.ClassId}) has no managed type");
            return null;
        }
        var m = (UObject)RuntimeHelpers.GetUninitializedObject(t);
        var a = Registry.Add(new NAsset { File = f, PathId = info.PathId, IsAsset = true, ClassId = info.ClassId }, m);
        loaded[(f, info.PathId)] = a;
        var r = f.Reader(info);
        if (dumpTree != null && dumpTree == info.ClassId.ToString() && treesDumped.Add(info.ClassId) && f.TreeOf(info) is { } tree)
            DumpTree(tree.Root, 0);
        if (Readers.TryGetValue(info.ClassId, out var reader)) {
            try { reader(a, r); } catch (Exception e) { Log.Exception(e, $"reading {ClassIds.Name(info.ClassId)} {f.Name}:{info.PathId}"); }
        } else if (IsNamed(info.ClassId)) {
            a.Name = r.String();
            Log.Once("reader" + info.ClassId, $"no reader for {ClassIds.Name(info.ClassId)} assets (first: '{a.Name}')");
        }
        return a;
    }

    static readonly string? dumpTree = Environment.GetEnvironmentVariable("FF9_DUMP_TREE");
    static readonly HashSet<int> treesDumped = new();

    static void DumpTree(TypeTree.Node n, int depth) {
        Log.Info($"{new string(' ', depth * 2)}{n.Type} {n.Name} size={n.ByteSize}{(n.Align ? " align" : "")}");
        if (depth < 4) foreach (var c in n.Children) DumpTree(c, depth + 1);
    }

    static bool IsNamed(int c) => c is 21 or 28 or 43 or 48 or 49 or 74 or 83 or 84 or 89 or 91 or 117 or 128 or 213 or 221 or 152 or 62 or 134 or 90 or 200;

    static UObject? LoadScriptableObject(SerializedFile f, ObjectInfo info) {
        var r = f.Reader(info);
        r.PPtr();
        bool enabled = r.U8() != 0; r.Align4();
        var (sfi, spi) = r.PPtr();
        string name = r.String();
        var type = ScriptType(f, sfi, spi);
        if (type == null) return null;
        var m = (UObject)RuntimeHelpers.GetUninitializedObject(type);
        var a = Registry.Add(new NAsset { File = f, PathId = info.PathId, IsAsset = true, Name = name, ClassId = ClassIds.MonoBehaviour }, m);
        loaded[(f, info.PathId)] = a;
        Scripts.RunConstructor(m);
        Scripts.ReadFields(m, r, f, new AssetResolver(), f.TreeOf(info));
        if (m is ScriptableObject) {
            var mi = Scripting.ScriptMethods.Find(type, "OnEnable");
            try { mi?.Invoke(m, null); } catch (TargetInvocationException e) { Log.Exception(e.InnerException!, type.Name + ".OnEnable"); }
        }
        return m;
    }

    // ---- MonoScript -> System.Type ----
    static readonly Dictionary<(SerializedFile, long), (Type? type, int order)> scripts = new();

    public static Type? ScriptType(SerializedFile from, int fileId, long pathId) => Script(from, fileId, pathId).type;

    public static (Type? type, int order) Script(SerializedFile from, int fileId, long pathId) {
        var f = External(from, fileId);
        if (f == null) return (null, 0);
        if (scripts.TryGetValue((f, pathId), out var s)) return s;
        s = (null, 0);
        if (f.Objects.TryGetValue(pathId, out var info) && info.ClassId == ClassIds.MonoScript) {
            var r = f.Reader(info);
            string name = r.String();
            int order = r.I32();
            r.Skip(16);
            string cls = r.String(), ns = r.String(), asm = r.String();
            string full = ns.Length > 0 ? ns + "." + cls : cls;
            Type? t = null;
            try {
                var a = Assembly.Load(Path.GetFileNameWithoutExtension(asm));
                t = a.GetType(full);
                // some copies of the game's data name a subclass the current Assembly-CSharp no longer
                // has: the naming screen's field is a UIInputPSVitaExtension (UIInput first, its own
                // fields after). Loaded as the base class, its UIInput is there and the name can be
                // typed (GitHub issue 6, a Galaxy Fold whose field was missing and showed no keyboard)
                if (t == null && cls.EndsWith("PSVitaExtension") && a.GetType(full[..^"PSVitaExtension".Length]) is { } baseType) {
                    Log.Once("script:" + full, $"script class {full} [{asm}] not found: loaded as {baseType.Name}");
                    t = baseType;
                }
            } catch (Exception e) { Log.Warn($"script assembly {asm}: {e.Message}"); }
            if (t == null) Log.Once("script:" + full, $"script class {full} [{asm}] not found");
            s = (t, order);
        }
        scripts[(f, pathId)] = s;
        return s;
    }
}

/// PPtrs in asset files: always through AssetDB.
public sealed class AssetResolver : IPPtrResolver {
    public UObject? Resolve(SerializedFile file, int fileId, long pathId, Type expected) {
        var f = AssetDB.External(file, fileId);
        var o = f == null ? null : AssetDB.Load(f, pathId);
        if (o == null && Environment.GetEnvironmentVariable("FF9_TRACE_PPTR") != null)
            Log.Once($"pptr{file.Name}:{fileId}", $"unresolved {expected.Name} in {file.Name}: file {fileId} ({f?.Name ?? "not found"}) path {pathId}");
        return o;
    }
}

public static class Scripts {
    public static void RunConstructor(UObject m) {
        var c = m.GetType().GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
        try { c?.Invoke(m, null); } catch (TargetInvocationException e) { Log.Exception(e.InnerException!, m.GetType().Name + "..ctor"); }
    }

    static readonly HashSet<Type> sizeWarned = new();

    public static void ReadFields(UObject m, BinReader r, SerializedFile f, IPPtrResolver resolver, TypeTree? tree = null) {
        var ctx = new Fields.Ctx { File = f, Resolver = resolver };
        var t = m.GetType();
        if (tree != null) {
            try { TreeFields.ReadScript(m, tree.Root, r, ctx); }
            catch (Exception e) { if (sizeWarned.Add(t)) Log.Exception(e, $"reading fields of {t.FullName} (type tree)"); }
            return;
        }
        try {
            Fields.Read(m, t, r, ctx);
            if (r.Pos < r.End && sizeWarned.Add(t))
                Log.Warn($"serialized size mismatch for {t.FullName}: {r.End - r.Pos} bytes left");
        } catch (Exception e) {
            if (sizeWarned.Add(t)) Log.Exception(e, $"reading fields of {t.FullName}");
        }
    }
}
