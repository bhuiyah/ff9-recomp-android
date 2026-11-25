// UnityEngine.Object / GameObject / Component / Behaviour / MonoBehaviour internal calls.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FF9Engine.Assets;
using FF9Engine.Core;
using FF9Engine.Scripting;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FF9Engine.Icalls;

static class ObjectIcalls {
    [Icall("UnityEngine.Object::get_name")]
    static string GetName(UObject self) => Registry.Of<NObject>(self) switch {
        // a shader's name is its ShaderLab name ("Unlit/Transparent Colored"), not the asset's
        // ("Unlit - Transparent Colored"): NGUI makes the clipped shader's name from it
        // ("Hidden/Unlit/Transparent Colored 2"); with the asset's it found none and drew
        // clipped panels unclipped (a dialog's text outlived its shrinking box: issue 12)
        Assets.NAsset { Data: Gfx.NShader { Name.Length: > 0 } s } => s.Name,
        var o => o.Name,
    };

    [Icall("UnityEngine.Object::set_name")]
    static void SetName(UObject self, string v) {
        // Unity: a component's name is its GameObject's; keep the copies in step
        var o = Registry.Of<NObject>(self);
        var g = o as NGameObject ?? (o as NComponent)?.GameObject;
        GameObjectIcalls.HierarchyVersion++;
        o.Name = v ?? "";
        if (g == null) return;
        g.Name = o.Name;
        foreach (var c in g.Components) c.Name = o.Name;
    }

    [Icall("UnityEngine.Object::get_hideFlags")]
    static HideFlags GetHideFlags(UObject self) => Registry.Of<NObject>(self).HideFlags;

    [Icall("UnityEngine.Object::set_hideFlags")]
    static void SetHideFlags(UObject self, HideFlags v) => Registry.Of<NObject>(self).HideFlags = v;

    [Icall("UnityEngine.Object::ToString")]
    static string ToStr(UObject self) {
        var n = Registry.Get(self);
        return n == null ? "null" : $"{n.Name} ({self.GetType().FullName})";
    }

    [Icall("UnityEngine.Object::DoesObjectWithInstanceIDExist")]
    static bool Exists(int id) => Registry.ById(id) != null;

    [Icall("UnityEngine.Object::DontDestroyOnLoad")]
    [Icall("UnityEngine.Application::DontDestroyOnLoad")]
    static void DontDestroyOnLoad(UObject target) {
        var n = Registry.Get(target);
        if (n is NComponent c) n = c.GameObject;
        if (n is NGameObject go) go.Transform.Root.GameObject.DontDestroy = true;
        else if (n != null) n.DontDestroy = true;
    }

    [Icall("UnityEngine.Object::Destroy")]
    [Icall("UnityEngine.Object::DestroyObject")]
    static void Destroy(UObject obj, float t) {
        var n = Registry.Get(obj);
        if (n == null) return;
        if (n.IsAsset) { Log.Warn($"Destroy on asset {n} ignored"); return; }
        if (n is NMonoBehaviour mb && t <= 0) mb.Enabled = mb.Enabled;   // OnDisable happens at destroy time
        Destroyer.Destroy(n, t);
        // A list's rows (RecycleListPopulator.RefreshPool) leave at once: until the end of the frame the
        // discarded ones still answered as the cursor's row, and their Start scrolled the new list to
        // where they were (a blank Summon list; a cursor part way down: GitHub #61).
        if (t <= 0 && !keepOldRows && n is NGameObject go && go.Managed is GameObject m && go.Components.Any(c => c.Managed is RecycleListItem)) GameObjectIcalls.SetActive(m, false);
    }
    static readonly bool keepOldRows = Environment.GetEnvironmentVariable("FF9_TEST_KEEPROWS") != null;   // tests: as before

    [Icall("UnityEngine.Object::DestroyImmediate")]
    static void DestroyImmediate(UObject obj, bool allowAssets) {
        var n = Registry.Get(obj);
        if (n == null) return;
        if (n.IsAsset && !allowAssets) return;
        Destroyer.DestroyNow(n);
    }

    static UObject[] Find(Type type, bool includeAssets, bool includeInactive) {
        var list = new List<UObject>();
        foreach (var n in Registry.All) {
            if (n.Managed is not { } m || !type.IsInstanceOfType(m)) continue;
            if (n.IsAsset && !includeAssets) continue;
            if (!includeInactive) {
                if (n is NComponent c && !c.GameObject.ActiveInHierarchy) continue;
                if (n is NGameObject g && !g.ActiveInHierarchy) continue;
            }
            list.Add(m);
        }
        var a = (UObject[])Array.CreateInstance(type.IsInterface || !typeof(UObject).IsAssignableFrom(type) ? typeof(UObject) : type, list.Count);
        for (int i = 0; i < list.Count; i++) a[i] = list[i];
        return a;
    }

    [Icall("UnityEngine.Object::FindObjectsOfType")]
    [Icall("UnityEngine.Object::FindSceneObjectsOfType")]
    static UObject[] FindObjectsOfType(Type type) => Find(type, false, false);

    [Icall("UnityEngine.Object::FindObjectsOfTypeIncludingAssets")]
    [Icall("UnityEngine.Resources::FindObjectsOfTypeAll")]
    static UObject[] FindAll(Type type) => Find(type, true, true);

    [Icall("UnityEngine.Object::Internal_CloneSingle")]
    static UObject Clone(UObject data) => Cloner.Clone(data, null, null);

    [Icall("UnityEngine.Object::INTERNAL_CALL_Internal_InstantiateSingle")]
    static UObject Instantiate(UObject data, ref Vector3 pos, ref Quaternion rot) => Cloner.Clone(data, pos, rot);
}

public static class GameObjectIcalls {
    static NGameObject G(GameObject g) => Registry.Of<NGameObject>(g);

    [Icall("UnityEngine.GameObject::Internal_CreateGameObject")]
    static void Create(GameObject mono, string name) {
        var go = Registry.Add(new NGameObject { Name = name ?? "New Game Object", Scene = Player.ActiveScene, ClassId = 1 }, mono);
        var t = Registry.Add(new NTransform { GameObject = go, ClassId = 4, Name = go.Name }, (UObject)RuntimeHelpers.GetUninitializedObject(typeof(Transform)));
        go.Components.Add(t);
    }

    [Icall("UnityEngine.GameObject::get_transform")]
    static Transform GetTransform(GameObject self) => (Transform)G(self).Transform.Managed!;

    [Icall("UnityEngine.GameObject::get_layer")] static int GetLayer(GameObject s) => G(s).Layer;
    [Icall("UnityEngine.GameObject::set_layer")] static void SetLayer(GameObject s, int v) => G(s).Layer = v;
    [Icall("UnityEngine.GameObject::get_activeSelf")] static bool ActiveSelf(GameObject s) => G(s).ActiveSelf;
    [Icall("UnityEngine.GameObject::get_active")]
    [Icall("UnityEngine.GameObject::get_activeInHierarchy")] static bool ActiveInHierarchy(GameObject s) => G(s).ActiveInHierarchy;
    [Icall("UnityEngine.GameObject::get_isStatic")] static bool IsStatic(GameObject s) => G(s).IsStatic;
    [Icall("UnityEngine.GameObject::set_isStatic")] static void SetStatic(GameObject s, bool v) => G(s).IsStatic = v;
    [Icall("UnityEngine.GameObject::get_isStaticBatchable")] static bool Batchable(GameObject s) => false;
    [Icall("UnityEngine.GameObject::get_tag")] static string GetTag(GameObject s) => G(s).Tag;
    [Icall("UnityEngine.GameObject::set_tag")] static void SetTag(GameObject s, string v) => G(s).Tag = v;
    [Icall("UnityEngine.GameObject::CompareTag")] static bool CompareTag(GameObject s, string t) => G(s).Tag == t;

    [Icall("UnityEngine.GameObject::SetActive")]
    [Icall("UnityEngine.GameObject::set_active")]
    public static void SetActive(GameObject self, bool v) {
        var g = G(self);
        if (g.ActiveSelf == v) return;
        g.ActiveSelf = v;
        if (!g.IsAsset) Player.HierarchyActivationChanged(g);
    }

    [Icall("UnityEngine.GameObject::SetActiveRecursively")]
    static void SetActiveRecursively(GameObject self, bool v) {
        void Rec(NTransform t) { t.GameObject.ActiveSelf = v; foreach (var c in t.Children) Rec(c); }
        var g = G(self);
        Rec(g.Transform);
        Player.HierarchyActivationChanged(g);
    }

    [Icall("UnityEngine.GameObject::Find")]
    static GameObject? Find(string name) { long pt = Core.Prof.T0(); var r = FindImpl(name); Core.Prof.Call(7, pt); return r; }
    // GameObject.Find by plain name: the last answer is kept while it is still the first match. Ids only
    // grow, so a newer object can't come before it; activations, renames and reparenting (which could
    // bring an older object in) bump HierarchyVersion and empty the cache. Battle effects look up
    // "Battle Camera" several times a frame, and each scan walked every object.
    public static int HierarchyVersion;
    static readonly Dictionary<string, NGameObject> findCache = new();
    static int findCacheVersion;

    static GameObject? FindImpl(string name) {
        if (string.IsNullOrEmpty(name)) return null;
        if (findCacheVersion != HierarchyVersion) { findCache.Clear(); findCacheVersion = HierarchyVersion; }
        if (!name.Contains('/') && findCache.TryGetValue(name, out var hit)) {
            if (hit.Managed != null && Registry.ById(hit.Id) == hit && hit.Name == name && hit.ActiveInHierarchy) return (GameObject)hit.Managed;
            findCache.Remove(name);
        }
        if (name.Contains('/')) {
            string p = name.TrimStart('/');
            string first = p.Split('/')[0];
            foreach (var n in Registry.All)
                if (n is NGameObject g && !g.IsAsset && g.Transform.Parent == null && g.Name == first && g.ActiveInHierarchy) {
                    var t = p.Length > first.Length ? g.Transform.Find(p[(first.Length + 1)..]) : g.Transform;
                    if (t != null && t.GameObject.ActiveInHierarchy) return (GameObject)t.GameObject.Managed!;
                }
            return null;
        }
        foreach (var n in Registry.All)
            if (n is NGameObject g && !g.IsAsset && g.Name == name && g.ActiveInHierarchy) { findCache[name] = g; return (GameObject)g.Managed!; }
        return null;
    }

    [Icall("UnityEngine.GameObject::FindGameObjectWithTag")]
    static GameObject? FindWithTag(string tag) =>
        Registry.All.OfType<NGameObject>().FirstOrDefault(g => !g.IsAsset && g.Tag == tag && g.ActiveInHierarchy)?.Managed as GameObject;

    [Icall("UnityEngine.GameObject::FindGameObjectsWithTag")]
    static GameObject[] FindAllWithTag(string tag) =>
        Registry.All.OfType<NGameObject>().Where(g => !g.IsAsset && g.Tag == tag && g.ActiveInHierarchy).Select(g => (GameObject)g.Managed!).ToArray();

    public static NComponent? FirstOf(NGameObject g, Type type) {
        foreach (var c in g.Components) if (c.Managed != null && type.IsInstanceOfType(c.Managed)) return c;
        return null;
    }

    [Icall("UnityEngine.GameObject::GetComponent")]
    static Component? GetComponent(GameObject self, Type type) => FirstOf(G(self), type)?.Managed as Component;

    [Icall("UnityEngine.GameObject::GetComponentFastPath")]
    static unsafe void GetComponentFastPath(GameObject self, Type type, IntPtr oneFurther) =>
        Unsafe.AsRef<object?>((void*)(oneFurther - IntPtr.Size)) = FirstOf(G(self), type)?.Managed;

    [Icall("UnityEngine.GameObject::GetComponentByName")]
    static Component? GetComponentByName(GameObject self, string name) =>
        G(self).Components.FirstOrDefault(c => c.Managed?.GetType().Name == name)?.Managed as Component;

    [Icall("UnityEngine.GameObject::GetComponentsInternal")]
    public static Array? GetComponentsInternal(GameObject self, Type type, bool useSearchTypeAsArrayReturnType, bool recursive,
                                               bool includeInactive, bool reverse, object? resultList) {
        var found = new List<Component>();
        void Add(NGameObject g) {
            foreach (var c in g.Components) if (c.Managed is Component m && type.IsInstanceOfType(m)) found.Add(m);
        }
        var start = G(self);
        if (!recursive) Add(start);
        else if (reverse) {
            for (var t = start.Transform; t != null; t = t.Parent)
                if (includeInactive || t.GameObject.ActiveInHierarchy) Add(t.GameObject);
        } else {
            void Rec(NTransform t) {
                if (!includeInactive && !t.GameObject.ActiveInHierarchy) return;
                Add(t.GameObject);
                foreach (var ch in t.Children) Rec(ch);
            }
            Rec(start.Transform);
        }
        if (resultList is IList l) {
            l.Clear();
            foreach (var c in found) l.Add(c);
            return null;
        }
        var a = Array.CreateInstance(useSearchTypeAsArrayReturnType ? type : typeof(Component), found.Count);
        for (int i = 0; i < found.Count; i++) a.SetValue(found[i], i);
        return a;
    }

    [Icall("UnityEngine.GameObject::Internal_AddComponentWithType")]
    static Component? AddComponent(GameObject self, Type type) => (Component?)Add(G(self), type);

    [Icall("UnityEngine.GameObject::AddComponentInternal")]
    static Component? AddComponentByName(GameObject self, string className) {
        var t = typeof(UObject).Assembly.GetType("UnityEngine." + className) ?? AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(className)).FirstOrDefault(x => x != null);
        return t == null ? null : (Component?)Add(G(self), t);
    }

    public static UObject? Add(NGameObject g, Type type) {
        foreach (var rc in type.GetCustomAttributes<RequireComponent>(true))
            foreach (var req in new[] { rc.m_Type0, rc.m_Type1, rc.m_Type2 })
                if (req != null && FirstOf(g, req) == null) Add(g, req);
        if (typeof(MonoBehaviour).IsAssignableFrom(type)) {
            if (type.IsAbstract) { Log.Warn($"AddComponent: {type} is abstract"); return null; }
            var m = (UObject)RuntimeHelpers.GetUninitializedObject(type);
            var mb = Registry.Add(new NMonoBehaviour { GameObject = g, ClassId = 114, Name = g.Name, IsAsset = g.IsAsset }, m);
            g.Components.Add(mb);
            Scripts.RunConstructor(m);
            Player.AddBehaviour(mb);
            if (!g.IsAsset) Player.Awaken(mb);
            return m;
        }
        int cid = ClassIds.ForType(type);
        if (cid == ClassIds.Transform) return g.Transform.Managed;
        var c = Components.New(cid);
        c.GameObject = g;
        c.Name = g.Name;
        c.ClassId = cid;
        c.IsAsset = g.IsAsset;
        var cm = (UObject)RuntimeHelpers.GetUninitializedObject(type);
        Registry.Add(c, cm);
        g.Components.Add(c);
        ComponentEvents.Added?.Invoke(c);
        return cm;
    }

    // ---- messages ----
    public static void Send(NGameObject g, string method, object? value, SendMessageOptions opts) {
        bool any = false;
        if (!g.ActiveInHierarchy) return; // Unity: messages are not sent to inactive objects
        foreach (var c in g.Components.ToArray()) {
            if (c is not NMonoBehaviour mb || mb.Managed == null) continue;
            var mi = Find(mb.Managed.GetType(), method, value);
            if (mi == null) continue;
            any = true;
            try { mi.Invoke(mb.Managed, mi.GetParameters().Length == 1 ? new[] { value } : null); }
            catch (TargetInvocationException e) { Log.Exception(e.InnerException!, $"SendMessage {method}"); }
        }
        if (!any && opts == SendMessageOptions.RequireReceiver)
            Log.Info($"[log error] SendMessage {method} has no receiver on '{g.Name}'");
    }

    static MethodInfo? Find(Type t, string name, object? value) {
        for (var c = t; c != null && c != typeof(MonoBehaviour); c = c.BaseType)
            foreach (var mi in c.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                if (mi.Name != name || mi.IsGenericMethodDefinition) continue;
                var ps = mi.GetParameters();
                if (ps.Length == 0 || ps.Length == 1 && (value == null ? !ps[0].ParameterType.IsValueType : ps[0].ParameterType.IsInstanceOfType(value)))
                    return mi;
            }
        return null;
    }

    [Icall("UnityEngine.GameObject::SendMessage")]
    static void SendMessage(GameObject self, string m, object v, SendMessageOptions o) => Send(G(self), m, v, o);

    [Icall("UnityEngine.GameObject::SendMessageUpwards")]
    static void SendMessageUpwards(GameObject self, string m, object v, SendMessageOptions o) {
        for (var t = G(self).Transform; t != null; t = t.Parent) Send(t.GameObject, m, v, SendMessageOptions.DontRequireReceiver);
    }

    [Icall("UnityEngine.GameObject::BroadcastMessage")]
    static void BroadcastMessage(GameObject self, string m, object v, SendMessageOptions o) {
        void Rec(NTransform t) { if (t.GameObject.ActiveInHierarchy) { Send(t.GameObject, m, v, SendMessageOptions.DontRequireReceiver); foreach (var c in t.Children.ToArray()) Rec(c); } }
        Rec(G(self).Transform);
    }
}

public static class ComponentEvents {
    public static Action<NComponent>? Added;
}

static class ComponentIcalls {
    static NComponent C(Component c) => Registry.Of<NComponent>(c);

    [Icall("UnityEngine.Component::get_transform")]
    static Transform GetTransform(Component self) => (Transform)C(self).GameObject.Transform.Managed!;

    [Icall("UnityEngine.Component::get_gameObject")]
    static GameObject GetGameObject(Component self) => (GameObject)C(self).GameObject.Managed!;

    [Icall("UnityEngine.Component::GetComponentFastPath")]
    static unsafe void GetComponentFastPath(Component self, Type type, IntPtr oneFurther) =>
        Unsafe.AsRef<object?>((void*)(oneFurther - IntPtr.Size)) = GameObjectIcalls.FirstOf(C(self).GameObject, type)?.Managed;

    [Icall("UnityEngine.Component::GetComponent")]
    static Component? GetComponentByName(Component self, string name) =>
        C(self).GameObject.Components.FirstOrDefault(c => c.Managed?.GetType().Name == name)?.Managed as Component;

    [Icall("UnityEngine.Component::GetComponentsForListInternal")]
    static void GetComponentsForList(Component self, Type type, object list) {
        var l = (IList)list;
        l.Clear();
        foreach (var c in C(self).GameObject.Components) if (c.Managed != null && type.IsInstanceOfType(c.Managed)) l.Add(c.Managed);
    }

    [Icall("UnityEngine.Component::CompareTag")]
    static bool CompareTag(Component self, string tag) => C(self).GameObject.Tag == tag;

    [Icall("UnityEngine.Component::SendMessage")]
    static void SendMessage(Component self, string m, object v, SendMessageOptions o) => GameObjectIcalls.Send(C(self).GameObject, m, v, o);

    [Icall("UnityEngine.Component::SendMessageUpwards")]
    static void SendMessageUpwards(Component self, string m, object v, SendMessageOptions o) {
        for (var t = C(self).GameObject.Transform; t != null; t = t.Parent) GameObjectIcalls.Send(t.GameObject, m, v, SendMessageOptions.DontRequireReceiver);
    }

    [Icall("UnityEngine.Component::BroadcastMessage")]
    static void BroadcastMessage(Component self, string m, object v, SendMessageOptions o) {
        void Rec(NTransform t) { if (t.GameObject.ActiveInHierarchy) { GameObjectIcalls.Send(t.GameObject, m, v, SendMessageOptions.DontRequireReceiver); foreach (var c in t.Children.ToArray()) Rec(c); } }
        Rec(C(self).GameObject.Transform);
    }

    // ---- Behaviour ----
    [Icall("UnityEngine.Behaviour::get_enabled")]
    static bool GetEnabled(Behaviour self) => C(self).Enabled;

    [Icall("UnityEngine.Behaviour::set_enabled")]
    static void SetEnabled(Behaviour self, bool v) {
        var c = C(self);
        if (c.Enabled == v) return;
        c.Enabled = v;
        if (c is NMonoBehaviour mb && !mb.IsAsset) {
            if (v) Player.Awaken(mb); else Player.Disable(mb);
        }
    }

    [Icall("UnityEngine.Behaviour::get_isActiveAndEnabled")]
    static bool IsActiveAndEnabled(Behaviour self) { var c = C(self); return c.Enabled && c.GameObject.ActiveInHierarchy; }

    // ---- MonoBehaviour ----
    static NMonoBehaviour MB(MonoBehaviour m) => Registry.Of<NMonoBehaviour>(m);

    [Icall("UnityEngine.MonoBehaviour::.ctor")]
    static void Ctor(MonoBehaviour self) { }

    [Icall("UnityEngine.MonoBehaviour::StartCoroutine_Auto")]
    static Coroutine? StartCoroutine(MonoBehaviour self, IEnumerator routine) {
        var mb = MB(self);
        if (!mb.GameObject.ActiveInHierarchy) {
            Log.Info($"[log error] Coroutine couldn't be started because the game object '{mb.GameObject.Name}' is inactive!");
            return null;
        }
        return Coroutines.Start(mb, routine);
    }

    [Icall("UnityEngine.MonoBehaviour::StartCoroutine")]
    static Coroutine? StartCoroutineByName(MonoBehaviour self, string name, object value) {
        var mb = MB(self);
        var mi = ScriptMethods.Find(self.GetType(), name);
        if (mi == null) { Log.Warn($"StartCoroutine: no method {name}"); return null; }
        var e = mi.Invoke(self, mi.GetParameters().Length == 1 ? new[] { value } : null) as IEnumerator;
        if (e == null) return null;
        var co = Coroutines.Start(mb, e);
        if (Coroutines.Of(co) is { } st) st.Name = name;
        return co;
    }

    [Icall("UnityEngine.MonoBehaviour::StopCoroutine")]
    static void StopCoroutineByName(MonoBehaviour self, string name) => Coroutines.Stop(MB(self), c => c.Name == name);

    [Icall("UnityEngine.MonoBehaviour::StopCoroutineViaEnumerator_Auto")]
    static void StopCoroutineE(MonoBehaviour self, IEnumerator e) => Coroutines.Stop(MB(self), c => c.Root == e);

    [Icall("UnityEngine.MonoBehaviour::StopCoroutine_Auto")]
    static void StopCoroutineC(MonoBehaviour self, Coroutine co) { var s = Coroutines.Of(co); Coroutines.Stop(MB(self), c => c == s); }

    [Icall("UnityEngine.MonoBehaviour::StopAllCoroutines")]
    static void StopAll(MonoBehaviour self) => Coroutines.Stop(MB(self));

    [Icall("UnityEngine.MonoBehaviour::Invoke")]
    static void Invoke(MonoBehaviour self, string m, float t) => Coroutines.Invoke(MB(self), m, t, 0);

    [Icall("UnityEngine.MonoBehaviour::InvokeRepeating")]
    static void InvokeRepeating(MonoBehaviour self, string m, float t, float r) => Coroutines.Invoke(MB(self), m, t, r);

    [Icall("UnityEngine.MonoBehaviour::CancelInvoke")]
    static void CancelInvoke(MonoBehaviour self, string m) => Coroutines.CancelInvoke(MB(self), m);

    [Icall("UnityEngine.MonoBehaviour::Internal_CancelInvokeAll")]
    static void CancelInvokeAll(MonoBehaviour self) => Coroutines.CancelInvoke(MB(self), null);

    [Icall("UnityEngine.MonoBehaviour::IsInvoking")]
    static bool IsInvoking(MonoBehaviour self, string m) => Coroutines.IsInvoking(MB(self), m);

    [Icall("UnityEngine.MonoBehaviour::Internal_IsInvokingAll")]
    static bool IsInvokingAll(MonoBehaviour self) => Coroutines.IsInvoking(MB(self), null);

    [Icall("UnityEngine.MonoBehaviour::get_useGUILayout")] static bool GetUseGUILayout(MonoBehaviour s) => true;
    [Icall("UnityEngine.MonoBehaviour::set_useGUILayout")] static void SetUseGUILayout(MonoBehaviour s, bool v) { }

    // ---- ScriptableObject ----
    [Icall("UnityEngine.ScriptableObject::Internal_CreateScriptableObject")]
    static void CreateSO(ScriptableObject self) {
        if (Registry.Get(self) == null) Registry.Add(new NAsset { Name = "", ClassId = 114 }, self);
    }

    [Icall("UnityEngine.ScriptableObject::CreateInstanceFromType")]
    static ScriptableObject CreateInstance(Type t) {
        var m = (ScriptableObject)RuntimeHelpers.GetUninitializedObject(t);
        Registry.Add(new NAsset { Name = "", ClassId = 114 }, m);
        Scripts.RunConstructor(m);
        ScriptMethods.Find(t, "Awake")?.Invoke(m, null);
        ScriptMethods.Find(t, "OnEnable")?.Invoke(m, null);
        return m;
    }

    [Icall("UnityEngine.ScriptableObject::CreateInstance")]
    static ScriptableObject? CreateInstanceByName(string name) {
        var t = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(x => x != null);
        return t == null ? null : CreateInstance(t);
    }

    [Icall("UnityEngine.ScriptableObject::INTERNAL_CALL_SetDirty")]
    static void SetDirty(ScriptableObject self) { }
}
