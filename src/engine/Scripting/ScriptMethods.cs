// Unity "messages" (Awake, Start, Update, ...) found by name on a MonoBehaviour class, any
// access level, including base classes.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace FF9Engine.Scripting;

public sealed class ScriptMethods {
    public MethodInfo? Awake, OnEnable, Start, Update, LateUpdate, FixedUpdate, OnDisable, OnDestroy,
                       OnGUI, OnLevelWasLoaded, OnApplicationQuit, OnApplicationPause, OnApplicationFocus,
                       OnPreRender, OnPostRender, OnRenderObject, OnWillRenderObject, OnPreCull,
                       OnBecameVisible, OnBecameInvisible, OnTransformParentChanged, OnTransformChildrenChanged,
                       OnRectTransformDimensionsChange;

    const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    static readonly Dictionary<Type, ScriptMethods> cache = new();

    public static ScriptMethods For(Type t) {
        if (cache.TryGetValue(t, out var m)) return m;
        m = new ScriptMethods();
        foreach (var f in typeof(ScriptMethods).GetFields())
            f.SetValue(m, Find(t, f.Name));
        cache[t] = m;
        return m;
    }

    public static MethodInfo? Find(Type t, string name) {
        for (var c = t; c != null && c != typeof(UnityEngine.MonoBehaviour); c = c.BaseType)
            foreach (var mi in c.GetMethods(Inst))
                if (mi.Name == name && !mi.IsGenericMethodDefinition && mi.GetParameters().Length <= 1)
                    return mi;
        return null;
    }

    /// Compiled callers (no reflection per call): (instance, arg) -> return value or null.
    static readonly Dictionary<MethodInfo, Func<object, object?, object?>> invokers = new();

    static Func<object, object?, object?> Invoker(MethodInfo mi) {
        if (invokers.TryGetValue(mi, out var f)) return f;
        var ps = mi.GetParameters();
        var dm = new System.Reflection.Emit.DynamicMethod("call_" + mi.Name, typeof(object), new[] { typeof(object), typeof(object) }, mi.DeclaringType!.Module, true);
        var il = dm.GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        il.Emit(System.Reflection.Emit.OpCodes.Castclass, mi.DeclaringType!);
        if (ps.Length == 1) {
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
            var pt = ps[0].ParameterType;
            il.Emit(pt.IsValueType ? System.Reflection.Emit.OpCodes.Unbox_Any : System.Reflection.Emit.OpCodes.Castclass, pt);
        }
        il.Emit(mi.IsVirtual ? System.Reflection.Emit.OpCodes.Callvirt : System.Reflection.Emit.OpCodes.Call, mi);
        if (mi.ReturnType == typeof(void)) il.Emit(System.Reflection.Emit.OpCodes.Ldnull);
        else if (mi.ReturnType.IsValueType) il.Emit(System.Reflection.Emit.OpCodes.Box, mi.ReturnType);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        f = (Func<object, object?, object?>)dm.CreateDelegate(typeof(Func<object, object?, object?>));
        invokers[mi] = f;
        return f;
    }

    /// Calls a message; exceptions are logged and swallowed, as Unity does. A message that
    /// returns IEnumerator (e.g. "IEnumerator Start()") is started as a coroutine.
    static readonly bool traceMods = Environment.GetEnvironmentVariable("FF9_TRACE_MODCALLS") != null;

    public static void Call(Core.NMonoBehaviour mb, MethodInfo? mi, object? arg = null) {
        if (mi == null || mb.Managed == null) return;
        // FF9_TRACE_MODCALLS=1: each call into a mod's script (assemblies not the game's), so the
        // last line before a crash names it
        if (traceMods && mi.DeclaringType?.Assembly is { } asm && asm != typeof(FF9StateSystem).Assembly && asm.GetName().Name!.StartsWith("Memoria.Scripts"))
            Log.Info($"modcall {mi.DeclaringType!.FullName}.{mi.Name}");
        long p0 = Core.Prof.On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try {
            object? r = Invoker(mi)(mb.Managed, arg);
            if (r is IEnumerator e) Coroutines.Start(mb, e);
        } catch (Exception ex) {
            Log.Exception(ex is TargetInvocationException t ? t.InnerException ?? ex : ex, $"{mb.Managed!.GetType().Name}.{mi.Name}");
        }
        if (p0 != 0) Core.Prof.Script(mi, System.Diagnostics.Stopwatch.GetTimestamp() - p0);
    }
}
