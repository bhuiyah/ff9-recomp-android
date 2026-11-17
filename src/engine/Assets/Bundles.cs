// Resources (the build's ResourceManager table), TextAsset, asset bundles (UnityRaw 5.x, one
// serialized file each, memory-mapped), and WWW for local files.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using FF9Engine.Core;
using FF9Engine.Scripting;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FF9Engine.Assets;

public static class ResourcesDB {
    static Dictionary<string, List<(int fileId, long pathId)>>? table;

    static Dictionary<string, List<(int, long)>> Table {
        get {
            if (table != null) return table;
            table = new(StringComparer.OrdinalIgnoreCase);
            foreach (var o in AssetDB.Main.ObjectList) {
                if (o.ClassId != ClassIds.ResourceManager) continue;
                var r = AssetDB.Main.Reader(o);
                int n = r.I32();
                for (int i = 0; i < n; i++) {
                    string path = r.String();
                    var pp = r.PPtr();
                    if (!table.TryGetValue(path, out var l)) table[path] = l = new();
                    l.Add(pp);
                }
            }
            Log.Info($"resources: {table.Count} paths");
            return table;
        }
    }

    public static IEnumerable<UObject> Candidates(string path) {
        if (!Table.TryGetValue(path.Replace('\\', '/'), out var l)) yield break;
        foreach (var (fi, pi) in l) {
            var f = AssetDB.External(AssetDB.Main, fi);
            if (f == null) continue;
            var o = AssetDB.Load(f, pi);
            if (o != null) yield return o;
        }
    }

    static bool Matches(UObject o, Type t) {
        if (t.IsInstanceOfType(o)) return true;
        // a prefab asked for as a component: the component on its root
        if (o is GameObject && typeof(Component).IsAssignableFrom(t)) return true;
        return false;
    }

    static UObject? Pick(UObject o, Type t) {
        if (t.IsInstanceOfType(o)) return o;
        if (o is GameObject go && typeof(Component).IsAssignableFrom(t))
            return Icalls.GameObjectIcalls.FirstOf((NGameObject)Registry.Get(go)!, t)?.Managed;
        return null;
    }

    public static UObject? Load(string path, Type t) {
        foreach (var o in Candidates(path)) if (Matches(o, t) && Pick(o, t) is { } p) return p;
        return null;
    }

    public static UObject[] LoadAll(string path, Type t) {
        string prefix = path.Replace('\\', '/').TrimEnd('/');
        var list = new List<UObject>();
        foreach (var key in Table.Keys.ToList())
            if (prefix.Length == 0 || key.Equals(prefix, StringComparison.OrdinalIgnoreCase) || key.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                foreach (var o in Candidates(key)) if (Pick(o, t) is { } p) list.Add(p);
        var a = (UObject[])Array.CreateInstance(t.IsInterface || !typeof(UObject).IsAssignableFrom(t) ? typeof(UObject) : t, list.Count);
        for (int i = 0; i < list.Count; i++) a[i] = list[i];
        return a;
    }

    [Icall("UnityEngine.Resources::Load")]
    static UObject? LoadIcall(string path, Type t) {
        var o = Load(path, t ?? typeof(UObject));
        if (o == null && Environment.GetEnvironmentVariable("FF9_LOG_MISSING") != null) Log.Info($"Resources.Load({path}, {t?.Name}): missing");
        return o;
    }

    [Icall("UnityEngine.Resources::LoadAll")] static UObject[] LoadAllIcall(string path, Type t) => LoadAll(path, t ?? typeof(UObject));

    [Icall("UnityEngine.Resources::LoadAsync")]
    static ResourceRequest LoadAsync(string path, Type t) {
        var req = AsyncOps.Create<ResourceRequest>(out var op);
        // ResourceRequest { string m_Path; Type m_Type; } after AsyncOperation.m_Ptr: its .asset calls Resources.Load
        var f = typeof(ResourceRequest).GetField("m_Path", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        f?.SetValue(req, path);
        typeof(ResourceRequest).GetField("m_Type", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(req, t);
        op.Complete();
        return req;
    }

    [Icall("UnityEngine.Resources::GetBuiltinResource")]
    static UObject? GetBuiltin(Type t, string path) {
        var f = AssetDB.File("library/unity default resources");
        if (f == null) return null;
        string name = Path.GetFileNameWithoutExtension(path);
        foreach (var o in f.ObjectList) {
            var mt = ClassIds.ManagedType(o.ClassId);
            if (mt == null || !t.IsAssignableFrom(mt)) continue;
            var r = f.Reader(o);
            if (r.String() == name) return AssetDB.Load(f, o.PathId);
        }
        return null;
    }

    [Icall("UnityEngine.Resources::UnloadAsset")] static void Unload(UObject o) { }
    [Icall("UnityEngine.Resources::UnloadUnusedAssets")]
    static AsyncOperation UnloadUnused() { Unloader.Run(); var m = AsyncOps.Create<AsyncOperation>(out var op); op.Complete(); return m; }
}

public static class TextAssets {
    public static void Register() => AssetDB.Readers[ClassIds.TextAsset] = (a, r) => {
        a.Name = r.String();
        int n = r.I32();
        a.Data = r.Bytes(n);
    };

    static byte[] B(TextAsset t) => Registry.Get(t) is NAsset { Data: byte[] b } ? b : Array.Empty<byte>();

    [Icall("UnityEngine.TextAsset::get_bytes")] static byte[] Bytes(TextAsset t) => B(t);

    [Icall("UnityEngine.TextAsset::get_text")]
    static string Text(TextAsset t) {
        var b = B(t);
        int skip = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(b, skip, b.Length - skip);
    }
}

public sealed class NBundle : NObject {
    public SerializedFile File = null!;
    public readonly Dictionary<string, List<(int fi, long pi)>> Container = new(StringComparer.OrdinalIgnoreCase);
    public (int fi, long pi) MainAsset;
    public string Path = "";
}

public static class Bundles {
    static readonly Dictionary<string, NBundle> open = new(StringComparer.OrdinalIgnoreCase);

    /// UnityRaw (format 3): signature, version, player/engine versions, sizes, then a directory of
    /// files (name, offset, size) relative to the header size.
    public static NBundle? OpenFile(string path) {
        string full = Path.GetFullPath(path);
        if (open.TryGetValue(full, out var b) && Registry.Get(b.Managed) == b) return b;
        if (!System.IO.File.Exists(full)) { Log.Warn($"asset bundle missing: {path}"); return null; }
        var mem = Mem.Map(full);
        var r = new BinReader(mem, 0) { BigEndian = true };
        string sig = r.CString();
        if (sig != "UnityRaw" && sig != "UnityWeb") { Log.Warn($"{path}: unsupported bundle signature {sig}"); return null; }
        int fmt = r.I32();
        r.CString(); r.CString();
        r.U32();
        int headerSize = r.I32();
        if (sig == "UnityWeb") {
            // LZMA stream after the header: the directory and files, offsets relative to its start
            var raw = System.IO.File.ReadAllBytes(full);
            mem.Dispose();
            mem = new Mem(Lzma.Decode(raw, headerSize));
            headerSize = 0;
        }
        var dir = new BinReader(mem, headerSize) { BigEndian = true };
        int n = dir.I32();
        SerializedFile? sf = null;
        for (int i = 0; i < n; i++) {
            string name = dir.CString();
            uint off = dir.U32(), size = dir.U32();
            if (name.EndsWith(".resS") || name.EndsWith(".resource")) continue;
            var f = new SerializedFile(name, mem, headerSize + off);
            AssetDB.Register(f, name);
            AssetDB.Register(f, $"archive:/{name}/{name}");
            sf ??= f;
        }
        if (sf == null) return null;
        b = new NBundle { File = sf, Path = full, Name = Path.GetFileName(path), IsAsset = true, ClassId = ClassIds.AssetBundle };
        foreach (var o in sf.ObjectList) {
            if (o.ClassId != ClassIds.AssetBundle) continue;
            var ar = sf.Reader(o);
            b.Name = ar.String();
            int np = ar.I32(); ar.Skip(np * 12L);
            int nc = ar.I32();
            for (int i = 0; i < nc; i++) {
                string key = ar.String();
                ar.I32(); ar.I32();
                var pp = ar.PPtr();
                if (!b.Container.TryGetValue(key, out var l)) b.Container[key] = l = new();
                l.Add(pp);
            }
            ar.I32(); ar.I32();
            b.MainAsset = ar.PPtr();
            break;
        }
        var m = (AssetBundle)RuntimeHelpers.GetUninitializedObject(typeof(AssetBundle));
        Registry.Add(b, m);
        open[full] = b;
        Log.Info($"bundle {Path.GetFileName(path)}: {b.Container.Count} assets");
        return b;
    }

    static NBundle B(AssetBundle a) => Registry.Of<NBundle>(a);

    static IEnumerable<UObject> Find(NBundle b, string name) {
        if (!b.Container.TryGetValue(name, out var l)) {
            // also by file name without extension
            string n = name.Replace('\\', '/');
            var key = b.Container.Keys.FirstOrDefault(k => k.EndsWith("/" + n, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(k).Equals(n, StringComparison.OrdinalIgnoreCase) ||
                Path.ChangeExtension(k, null)!.EndsWith("/" + n, StringComparison.OrdinalIgnoreCase));
            if (key == null) yield break;
            l = b.Container[key];
        }
        foreach (var (fi, pi) in l) {
            var f = AssetDB.External(b.File, fi);
            if (f != null && AssetDB.Load(f, pi) is { } o) yield return o;
        }
    }

    static UObject? Pick(IEnumerable<UObject> objs, Type t) {
        foreach (var o in objs) {
            if (t.IsInstanceOfType(o)) return o;
            if (o is GameObject go && typeof(Component).IsAssignableFrom(t))
                return Icalls.GameObjectIcalls.FirstOf((NGameObject)Registry.Get(go)!, t)?.Managed;
        }
        return null;
    }

    [Icall("UnityEngine.AssetBundle::CreateFromFile")] static AssetBundle? CreateFromFile(string path) => OpenFile(path)?.Managed as AssetBundle;

    [Icall("UnityEngine.AssetBundle::CreateFromMemoryImmediate")]
    static AssetBundle? FromMemory(byte[] data) {
        string tmp = Path.Combine(Directory.GetCurrentDirectory(), "cache", $"bundle{data.Length:x}_{data.GetHashCode():x}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        System.IO.File.WriteAllBytes(tmp, data);
        return OpenFile(tmp)?.Managed as AssetBundle;
    }

    [Icall("UnityEngine.AssetBundle::CreateFromMemory")]
    static AssetBundleCreateRequest FromMemoryAsync(byte[] data) {
        var req = AsyncOps.Create<AssetBundleCreateRequest>(out var op);
        op.Result = FromMemory(data);
        op.Complete();
        return req;
    }

    [Icall("UnityEngine.AssetBundleCreateRequest::get_assetBundle")]
    static AssetBundle? CreateReqBundle(AssetBundleCreateRequest r) => AsyncOps.Of(r)?.Result as AssetBundle;
    [Icall("UnityEngine.AssetBundleCreateRequest::DisableCompatibilityChecks")] static void NoChecks(AssetBundleCreateRequest r) { }

    [Icall("UnityEngine.AssetBundle::get_mainAsset")]
    static UObject? MainAsset(AssetBundle a) {
        var b = B(a);
        var f = AssetDB.External(b.File, b.MainAsset.fi);
        return f == null || b.MainAsset.pi == 0 ? null : AssetDB.Load(f, b.MainAsset.pi);
    }

    [Icall("UnityEngine.AssetBundle::Contains")] static bool Contains(AssetBundle a, string name) => Find(B(a), name).Any();
    [Icall("UnityEngine.AssetBundle::Load")]
    [Icall("UnityEngine.AssetBundle::LoadAsset_Internal")] static UObject? Load(AssetBundle a, string name, Type t) => Pick(Find(B(a), name), t ?? typeof(UObject));

    [Icall("UnityEngine.AssetBundle::LoadAsync")]
    [Icall("UnityEngine.AssetBundle::LoadAssetAsync_Internal")]
    static AssetBundleRequest LoadAsync(AssetBundle a, string name, Type t) {
        var req = AsyncOps.Create<AssetBundleRequest>(out var op);
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(AssetBundleRequest).GetField("m_AssetBundle", F)?.SetValue(req, a);
        typeof(AssetBundleRequest).GetField("m_Path", F)?.SetValue(req, name);
        typeof(AssetBundleRequest).GetField("m_Type", F)?.SetValue(req, t);
        op.Complete();
        return req;
    }

    [Icall("UnityEngine.AssetBundle::LoadAssetWithSubAssets_Internal")]
    static UObject[] LoadWithSub(AssetBundle a, string name, Type t) {
        var l = Find(B(a), name).Where(o => (t ?? typeof(UObject)).IsInstanceOfType(o)).ToList();
        var arr = (UObject[])Array.CreateInstance(t == null || t.IsInterface ? typeof(UObject) : t, l.Count);
        for (int i = 0; i < l.Count; i++) arr[i] = l[i];
        return arr;
    }

    [Icall("UnityEngine.AssetBundle::LoadAll")]
    static UObject[] LoadAll(AssetBundle a, Type t) {
        var b = B(a);
        var l = new List<UObject>();
        foreach (var o in b.File.ObjectList) {
            var mt = ClassIds.ManagedType(o.ClassId);
            if (mt == null || o.ClassId == ClassIds.AssetBundle) continue;
            if (t != null && !t.IsAssignableFrom(mt) && o.ClassId != ClassIds.MonoBehaviour) continue;
            var x = AssetDB.Load(b.File, o.PathId);
            if (x != null && (t == null || t.IsInstanceOfType(x))) l.Add(x);
        }
        var arr = (UObject[])Array.CreateInstance(t == null || t.IsInterface ? typeof(UObject) : t, l.Count);
        for (int i = 0; i < l.Count; i++) arr[i] = l[i];
        return arr;
    }

    [Icall("UnityEngine.AssetBundle::Unload")]
    static void Unload(AssetBundle a, bool all) {
        var b = Registry.Get(a) as NBundle;
        if (b == null) return;
        open.Remove(b.Path);
        Registry.Remove(b);
    }

    [Icall("UnityEngine.AssetBundle::GetAllAssetNames")] static string[] Names(AssetBundle a) => B(a).Container.Keys.ToArray();
    [Icall("UnityEngine.AssetBundle::GetAllScenePaths")] static string[] Scenes(AssetBundle a) => Array.Empty<string>();
}

/// WWW for local files ("file://..." and plain paths): loaded at once.
public static class WWWs {
    sealed class Req { public string Url = ""; public byte[]? Bytes; public string? Error; public AssetBundle? Bundle; }

    static Req R(WWW w) => Native.Handles.Data<Req>(w);

    static string LocalPath(string url) {
        if (url.StartsWith("file:///")) return url[7..];
        if (url.StartsWith("file://")) return url[7..];
        if (url.StartsWith("jar:")) return url;
        return url;
    }

    static void Start(WWW w, string url) {
        var r = R(w);
        r.Url = url;
        string p = LocalPath(Uri.UnescapeDataString(url));
        try {
            if (url.StartsWith("http")) r.Error = "network access is not available";
            else r.Bytes = File.ReadAllBytes(p);
        } catch (Exception e) { r.Error = e.Message; }
    }

    [Icall("UnityEngine.WWW::InitWWW")] static void Init(WWW w, string url, byte[] post, string[] headers) => Start(w, url);
    [Icall("UnityEngine.WWW::INTERNAL_CALL_WWW")] static void InitCached(WWW w, string url, ref Hash128 h, uint crc) => Start(w, url);
    [Icall("UnityEngine.WWW::DestroyWWW")] static void Destroy(WWW w, bool cancel) { }
    [Icall("UnityEngine.WWW::enforceWebSecurityRestrictions")] static bool Sec(WWW w) => false;
    [Icall("UnityEngine.WWW::get_responseHeadersString")] static string Headers(WWW w) => "";
    [Icall("UnityEngine.WWW::get_bytes")] static byte[] Bytes(WWW w) => R(w).Bytes ?? Array.Empty<byte>();
    [Icall("UnityEngine.WWW::get_size")] static int Size(WWW w) => R(w).Bytes?.Length ?? 0;
    [Icall("UnityEngine.WWW::get_error")] static string? Error(WWW w) => R(w).Error;
    [Icall("UnityEngine.WWW::get_isDone")] static bool IsDone(WWW w) => true;
    [Icall("UnityEngine.WWW::get_progress")] static float Progress(WWW w) => 1;
    [Icall("UnityEngine.WWW::get_uploadProgress")] static float Up(WWW w) => 1;
    [Icall("UnityEngine.WWW::get_bytesDownloaded")] static int Down(WWW w) => R(w).Bytes?.Length ?? 0;
    [Icall("UnityEngine.WWW::get_url")] static string Url(WWW w) => R(w).Url;
    [Icall("UnityEngine.WWW::GetURL")] static string GetURL(string url) => url;
    [Icall("UnityEngine.WWW::get_threadPriority")] static UnityEngine.ThreadPriority GetPri(WWW w) => UnityEngine.ThreadPriority.Normal;
    [Icall("UnityEngine.WWW::set_threadPriority")] static void SetPri(WWW w, UnityEngine.ThreadPriority p) { }

    [Icall("UnityEngine.WWW::GetTexture")]
    static Texture2D GetTexture(WWW w, bool nonReadable) {
        var t = new Texture2D(2, 2);
        var r = R(w);
        if (r.Bytes != null) t.LoadImage(r.Bytes);
        return t;
    }

    [Icall("UnityEngine.WWW::LoadImageIntoTexture")]
    static void LoadInto(WWW w, Texture2D t) { var r = R(w); if (r.Bytes != null) t.LoadImage(r.Bytes); }

    [Icall("UnityEngine.WWW::get_assetBundle")]
    static AssetBundle? Bundle(WWW w) {
        var r = R(w);
        if (r.Bundle != null) return r.Bundle;
        string p = LocalPath(Uri.UnescapeDataString(r.Url));
        r.Bundle = File.Exists(p) ? Bundles.OpenFile(p)?.Managed as AssetBundle : null;
        return r.Bundle;
    }

    [Icall("UnityEngine.WWW::GetAudioClipInternal")]
    static AudioClip? Audio(WWW w, bool threeD, bool stream, bool compressed, AudioType t) { Log.Once("WWW.GetAudioClip"); return null; }
}
