// The player loop: scene loading, MonoBehaviour lifecycle and frames, in Unity 5.2's order.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using FF9Engine.Scripting;
using FF9Engine.Icalls;
using UnityEngine;

namespace FF9Engine.Core;

public static class Clock {
    static readonly Stopwatch sw = Stopwatch.StartNew();
    public static float Now;                // scaled game time (Time.time)
    public static float Unscaled;           // Time.unscaledTime
    public static float Delta = 1 / 60f, UnscaledDelta = 1 / 60f, SmoothDelta = 1 / 60f;
    public static float TimeScale = 1, FixedDelta = 0.02f, MaxDelta = 1 / 3f, FixedTime;
    public static float LevelLoadTime;
    public static int Frame;
    public static float Realtime => (float)sw.Elapsed.TotalSeconds;
    public static int TargetFrameRate = -1;

    static double last = -1;

    /// After a pause (no window) the next frame is a normal one, not one long delta.
    public static void Resync() => last = -1;

    public static void Tick() {
        double t = sw.Elapsed.TotalSeconds;
        float d = last < 0 ? 1 / 60f : (float)(t - last);
        last = t;
        UnscaledDelta = d;
        Unscaled += d;
        Delta = Math.Min(d, MaxDelta) * TimeScale;
        SmoothDelta = SmoothDelta * 0.8f + Delta * 0.2f;
        Now += Delta;
        Frame++;
    }
}

public sealed class NScene {
    public int Index;
    public string Name = "";
    public string Path = "";
}

public static class Player {
    public static readonly List<NMonoBehaviour> Behaviours = new();
    static readonly List<NMonoBehaviour> pendingStart = new();
    public static NScene? ActiveScene;
    public static string LastSceneLoaded = "";
    public static string[] SceneNames = Array.Empty<string>();
    public static bool Quit;
    public static Action? Render;             // set by the renderer
    public static Action? Present;            // shows the frame Render drew (after WaitForEndOfFrame's coroutines)
    public static Action? PollInput;
    public static Action<float>? AnimationTick;   // legacy Animation, between Update and LateUpdate

    sealed class LevelRequest { public int Index; public bool Additive; public AsyncOps.Op? Op; }
    static readonly Queue<LevelRequest> levelRequests = new();

    public static int Main(string[] args) {
        Native.DllImports.Install();
        Native.Compat.Install();
        Native.Compat.TestThreadStart();
        Native.HarmonyCompat.Watch();
        // FF9_TRACE_EXCEPTIONS=1 (files/env.txt): every exception as it is thrown, caught or not
        // (the game's save code catches its own and only reports a failure)
        if (Environment.GetEnvironmentVariable("FF9_TRACE_EXCEPTIONS") != null)
            AppDomain.CurrentDomain.FirstChanceException += (s, e) => Log.Info("thrown: " + e.Exception.GetType().Name + ": " + e.Exception.Message + "\n" + e.Exception.StackTrace);
        PollInput = () => { Touches.WasDown = Touches.Down; Icalls.Pad.Poll(); };
        Gfx.Textures.Register(); Gfx.Shaders.Register(); Gfx.Materials.Register(); Gfx.Meshes.Register();
        Gfx.Sprites.Register(); Gfx.RendererComponents.Register(); Gfx.Cameras.Register(); Assets.TextAssets.Register(); Physics.Colliders.Register(); Gfx.Canvases.Register(); Text.Fonts.Register(); Anim.Clips.Register(); Anim.Animations.Register();
        if (Environment.GetEnvironmentVariable("FF9_NOGL") == null) {
            try { Gfx.Device.Init(); } catch (Exception e) { Log.Warn("GL init failed: " + e.Message); }
            Icalls.Display.Width = Gfx.Device.Width; Icalls.Display.Height = Gfx.Device.Height;
        }
        string? shot = Environment.GetEnvironmentVariable("FF9_SHOT");     // "frame[,frame...]:dir"
        var shotFrames = new HashSet<int>();
        string shotDir = "shots";
        if (shot != null) {
            var sp = shot.Split(':');
            foreach (var f in sp[0].Split(',')) if (int.TryParse(f, out int fi)) shotFrames.Add(fi);
            if (sp.Length > 1) shotDir = sp[1];
        }
        int noRenderFrom = int.TryParse(Environment.GetEnvironmentVariable("FF9_NO_RENDER_FROM"), out var nrf) ? nrf : int.MaxValue;
        Render = () => {
            if (Clock.Frame >= noRenderFrom) return;
            long r0 = Stopwatch.GetTimestamp();
            Gfx.Pipeline.RenderFrame();
            Prof.Render += Stopwatch.GetTimestamp() - r0;
            if (shotFrames.Contains(Clock.Frame)) Gfx.Device.Screenshot(System.IO.Path.Combine(shotDir, $"frame{Clock.Frame:D5}.png"));
        };
        // WaitForEndOfFrame's coroutines run between the two, as in Unity: the frame is drawn but
        // not shown yet. They ran after the swap, when the back buffer is undefined (black on
        // Adreno), so the battle swirl (SFX_Rush.CreateScreen's ReadPixels) captured black
        // (GitHub issue 24).
        Present = () => {
            if (Clock.Frame >= noRenderFrom && Environment.GetEnvironmentVariable("FF9_NO_SWAP") != null) return;
            long p0 = Stopwatch.GetTimestamp();
            Gfx.Device.Present();
            Prof.Present += Stopwatch.GetTimestamp() - p0;
        };
        Assets.AssetDB.Init();
        SceneNames = Assets.AssetDB.SceneNames;
        LoadLevel(0, false);
        int maxFrames = int.TryParse(Environment.GetEnvironmentVariable("FF9_FRAMES"), out var mf) ? mf : int.MaxValue;
        // FF9_FPS=60: pace frames like a display's vsync (the headless pbuffer doesn't wait)
        int fps = int.TryParse(Environment.GetEnvironmentVariable("FF9_FPS"), out var fv) ? fv : 0;
        var pace = Stopwatch.StartNew();
        long next = 0;
        while (!Quit && Clock.Frame < maxFrames) {
            long f0 = Stopwatch.GetTimestamp();
            Frame();
            Prof.Frame(Stopwatch.GetTimestamp() - f0);
            // the game's Application.targetFrameRate, as Unity keeps it on Android: frames wait
            // (sleeping, not spinning) instead of running as fast as the display allows. Headless
            // tests run unpaced unless FF9_FPS asks.
            int target = fps > 0 ? fps : Gfx.Device.HasWindow && Clock.TargetFrameRate > 0 ? Clock.TargetFrameRate : 0;
            if (target > 0) {
                next += Stopwatch.Frequency / target;
                long wait = next - pace.ElapsedTicks;
                if (wait > 0) {
                    int ms = (int)(wait * 1000 / Stopwatch.Frequency);
                    if (ms > 1) System.Threading.Thread.Sleep(ms - 1);
                    while (pace.ElapsedTicks < next) System.Threading.Thread.Yield();
                }
                else if (wait < -Stopwatch.Frequency / 4) next = pace.ElapsedTicks;
            }
        }
        foreach (var b in Behaviours.ToArray()) ScriptMethods.Call(b, b.Methods.OnApplicationQuit);
        Log.Info($"player stopped after {Clock.Frame} frames");
        return 0;
    }

    public static void RequestLevel(int index, bool additive, AsyncOps.Op? op) =>
        levelRequests.Enqueue(new LevelRequest { Index = index, Additive = additive, Op = op });

    public static int SceneIndex(string name) {
        for (int i = 0; i < SceneNames.Length; i++) {
            string n = SceneNames[i];
            string shortName = System.IO.Path.GetFileNameWithoutExtension(n);
            if (string.Equals(shortName, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    static void Frame() {
        Native.FaultHandlers.Tick();
        Clock.Tick();
        long ps = Prof.On ? Stopwatch.GetTimestamp() : 0;
        PollInput?.Invoke();
        Prof.Part(0, ref ps);
        // FixedUpdate at a fixed rate
        while (Clock.FixedTime + Clock.FixedDelta <= Clock.Now) {
            Clock.FixedTime += Clock.FixedDelta;
            foreach (var b in Snapshot()) if (Runs(b)) ScriptMethods.Call(b, b.Methods.FixedUpdate);
            Coroutines.Run(Coroutines.Phase.Fixed);
        }
        Prof.Part(1, ref ps);
        Native.Compat.FieldTick();
        RunStarts();
        foreach (var b in Snapshot()) if (Runs(b) && b.Started) ScriptMethods.Call(b, b.Methods.Update);
        Prof.Part(2, ref ps);
        Coroutines.RunInvokes();
        Coroutines.Run(Coroutines.Phase.Update);
        Prof.Part(3, ref ps);
        AsyncOps.Tick();
        AnimationTick?.Invoke(Clock.Delta);
        Prof.Part(4, ref ps);
        foreach (var b in Snapshot()) if (Runs(b) && b.Started) ScriptMethods.Call(b, b.Methods.LateUpdate);
        Prof.Part(5, ref ps);
        Render?.Invoke();
        Prof.Part(6, ref ps);
        Coroutines.Run(Coroutines.Phase.EndOfFrame);
        Prof.Part(7, ref ps);
        Present?.Invoke();
        Prof.Part(8, ref ps);
        DebugDump.Tick();
        Companion.Tick();
        UiSounds.Tick();
        MusicTrace.Tick();
        ImmortalTest.Tick();
        HarmonyTest.Tick();
        TestLoad.Tick();
        TextInput.Tick();
        Widescreen.Tick();
        if (memEvery > 0 && Clock.Frame % memEvery == 0) MemStat();
        Prof.Part(9, ref ps);
        Destroyer.Flush();
        Prof.Part(10, ref ps);
        while (levelRequests.Count > 0) {
            var r = levelRequests.Dequeue();
            LoadLevel(r.Index, r.Additive);
            r.Op?.Complete();
        }
    }

    static NMonoBehaviour[] Snapshot() => Behaviours.ToArray();

    static readonly int memEvery = int.TryParse(Environment.GetEnvironmentVariable("FF9_MEMSTAT"), out var me) ? me : 0;

    /// FF9_MEMSTAT=n: every n frames, managed heap, process memory and object counts.
    static void MemStat() {
        string rss = "?", vsz = "?";
        int maps = 0;
        try {
            foreach (var l in System.IO.File.ReadAllLines("/proc/self/status")) {
                if (l.StartsWith("VmRSS:")) rss = l.Substring(6).Trim();
                else if (l.StartsWith("VmSize:")) vsz = l.Substring(7).Trim();
            }
            var ml = System.IO.File.ReadAllLines("/proc/self/maps");
            maps = ml.Length;
            if (Environment.GetEnvironmentVariable("FF9_MEMSTAT_MAPS") != null) {
                var groups = new Dictionary<string, int>();
                foreach (var l in ml) {
                    var parts = l.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
                    string key = (parts.Length > 5 ? parts[5] : "[anon]") + " " + parts[1];
                    groups[key] = groups.GetValueOrDefault(key) + 1;
                }
                foreach (var g in groups.OrderByDescending(g => g.Value).Take(6)) Log.Info($"   maps {g.Value} {g.Key}");
                var sizes = new Dictionary<long, int>();
                foreach (var l in ml) {
                    if (!l.Contains("kgsl")) continue;
                    var r = l.Split(' ')[0].Split('-');
                    long sz = Convert.ToInt64(r[1], 16) - Convert.ToInt64(r[0], 16);
                    sizes[sz] = sizes.GetValueOrDefault(sz) + 1;
                }
                Log.Info("   kgsl sizes: " + string.Join(", ", sizes.OrderByDescending(s => s.Value).Take(8).Select(s => $"{s.Key >> 10}K x{s.Value}")));
            }
        } catch { }
        Log.Info($"gpu since last: vbo {GpuStats.VboUploads}, ibo {GpuStats.IboUploads}, tex {GpuStats.TexUploads} ({GpuStats.LastTex}), rt {GpuStats.RtCreates}, programs {Gfx.Pipeline.ProgramCount}");
        GpuStats.VboUploads = GpuStats.IboUploads = GpuStats.TexUploads = GpuStats.RtCreates = 0;
        if (System.Runtime.InteropServices.NativeLibrary.TryGetExport(Native.DllImports.Host, "ff9_gl_stats", out var glStats))
            unsafe { ((delegate* unmanaged<void>)glStats)(); }
        if (Environment.GetEnvironmentVariable("FF9_MEMSTAT_TYPES") != null)
            Log.Info("   objects by type: " + string.Join(", ", Registry.All.GroupBy(o => o is Assets.NAsset a ? "NAsset:" + a.Data?.GetType().Name : o.GetType().Name + (o is NGameObject g ? ":" + g.Name : ""))
                .OrderByDescending(g => g.Count()).Take(10).Select(g => $"{g.Key} {g.Count()}")));
        Log.Info($"mem f{Clock.Frame}: gc {GC.GetTotalMemory(false) >> 20} MB (gen0 {GC.CollectionCount(0)}, gen2 {GC.CollectionCount(2)}), rss {rss}, vsz {vsz}, maps {maps}, behaviours {Behaviours.Count}, objects {Registry.Count}");
    }

    static bool Runs(NMonoBehaviour b) => b.Enabled && b.Managed != null && b.GameObject.ActiveInHierarchy && Registry.Get(b.Managed) == b;

    public static int PendingStarts => pendingStart.Count;

    static void RunStarts() {
        while (pendingStart.Count > 0) {
            var list = pendingStart.ToArray();
            pendingStart.Clear();
            foreach (var b in list.OrderBy(b => b.ExecutionOrder)) {
                if (b.Started || !Runs(b)) { if (!b.Started) pendingStart.Add(b); continue; }
                b.Started = true;
                ScriptMethods.Call(b, b.Methods.Start);
            }
            if (pendingStart.All(b => !Runs(b))) break;
        }
    }

    /// Registers a newly created MonoBehaviour (scene load, AddComponent, Instantiate).
    public static void AddBehaviour(NMonoBehaviour b) {
        b.Methods = ScriptMethods.For(b.Managed!.GetType());
        Behaviours.Add(b);
    }

    /// Awake + OnEnable for a behaviour whose GameObject is active (Unity does this at creation
    /// or when the GameObject is first activated).
    public static void Awaken(NMonoBehaviour b) {
        if (!b.GameObject.ActiveInHierarchy) return;
        if (!b.Awoken) {
            b.Awoken = true;
            ScriptMethods.Call(b, b.Methods.Awake);
        }
        if (b.Enabled && !b.EnabledCalled && Registry.Get(b.Managed) == b) {
            b.EnabledCalled = true;
            ScriptMethods.Call(b, b.Methods.OnEnable);
            if (!b.Started && !pendingStart.Contains(b)) pendingStart.Add(b);
        }
    }

    public static void Disable(NMonoBehaviour b) {
        if (!b.EnabledCalled) return;
        b.EnabledCalled = false;
        ScriptMethods.Call(b, b.Methods.OnDisable);
    }

    /// Activation changes on a hierarchy: OnEnable/Awake or OnDisable for behaviours below.
    public static void HierarchyActivationChanged(NGameObject go) {
        Icalls.GameObjectIcalls.HierarchyVersion++;
        var list = new List<NMonoBehaviour>();
        Collect(go.Transform, list);
        foreach (var b in list) {
            if (b.GameObject.ActiveInHierarchy) Awaken(b);
            else Disable(b);
        }
    }

    static void Collect(NTransform t, List<NMonoBehaviour> list) {
        foreach (var c in t.GameObject.Components) if (c is NMonoBehaviour b) list.Add(b);
        foreach (var ch in t.Children.ToArray()) Collect(ch, list);
    }

    static void LoadLevel(int index, bool additive) {
        var sw = Stopwatch.StartNew();
        if (!additive) {
            foreach (var o in Registry.All.ToArray())
                if (o is NGameObject go && !go.IsAsset && !go.DontDestroy && go.Transform.Parent == null && !IsUnderDontDestroy(go))
                    Destroyer.DestroyNow(go);
        }
        var scene = new NScene { Index = index, Path = SceneNames[index], Name = System.IO.Path.GetFileNameWithoutExtension(SceneNames[index]) };
        if (!additive) ActiveScene = scene;
        var created = Assets.SceneLoader.Load(index, scene);
        Clock.LevelLoadTime = Clock.Now;
        if (scene.Name is not ("Loading" or "SwirlScene")) LastSceneLoaded = scene.Name;   // the last real scene, not the in-between ones
        Log.Info($"scene {index} '{scene.Name}': {created.Count} behaviours, {sw.ElapsedMilliseconds} ms");
        foreach (var b in created.OrderBy(b => b.ExecutionOrder)) Awaken(b);
        foreach (var b in Snapshot()) if (Registry.Get(b.Managed) == b) ScriptMethods.Call(b, b.Methods.OnLevelWasLoaded, index);
    }

    static bool IsUnderDontDestroy(NGameObject go) => go.Transform.Root.GameObject.DontDestroy;
}

/// Object.Destroy: deferred to the end of the frame; DestroyImmediate: now.
public static class Destroyer {
    static readonly List<(NObject obj, float at)> pending = new();

    public static void Destroy(NObject o, float delay) => pending.Add((o, Clock.Now + delay));

    public static void Flush() {
        if (pending.Count == 0) return;
        var now = pending.Where(p => p.at <= Clock.Now).ToList();
        pending.RemoveAll(p => p.at <= Clock.Now);
        foreach (var (o, _) in now) DestroyNow(o);
    }

    public static void DestroyNow(NObject o) {
        if (Registry.ById(o.Id) != o) return;
        switch (o) {
            case NGameObject go:
                foreach (var ch in go.Transform.Children.ToArray()) DestroyNow(ch.GameObject);
                foreach (var c in go.Components.ToArray().Reverse()) {
                    if (c is NMonoBehaviour b) {
                        if (b.EnabledCalled) Player.Disable(b);
                        if (b.Awoken) ScriptMethods.Call(b, b.Methods.OnDestroy);
                        Coroutines.Stop(b);
                        Coroutines.CancelInvoke(b, null);
                        Player.Behaviours.Remove(b);
                    }
                    Registry.Remove(c);
                }
                go.Transform.Parent?.Children.Remove(go.Transform);
                Registry.Remove(go);
                break;
            case NTransform:
                break;          // a Transform is destroyed with its GameObject only
            case NComponent c:
                if (c is NMonoBehaviour mb) {
                    if (mb.EnabledCalled) Player.Disable(mb);
                    if (mb.Awoken) ScriptMethods.Call(mb, mb.Methods.OnDestroy);
                    Coroutines.Stop(mb);
                    Coroutines.CancelInvoke(mb, null);
                    Player.Behaviours.Remove(mb);
                }
                c.GameObject.Components.Remove(c);
                Registry.Remove(c);
                break;
            default:
                Registry.Remove(o);
                break;
        }
    }
}

/// Counts of GPU uploads between memory reports.
/// FF9_PROFILE=n (FF9_TRACE_PROFILE=n on a device): every n frames, the mean milliseconds per frame in game logic (scripts and
/// animation, without rendering), render submission (culling, sorting, GL calls) and the swap.
public static class Prof {
    static readonly int every = int.TryParse(Environment.GetEnvironmentVariable("FF9_PROFILE") ?? Environment.GetEnvironmentVariable("FF9_TRACE_PROFILE"), out var n) ? n : 0;
    public static long Render, Present;
    static long total, worst;
    static int frames;
    public static bool On => every > 0;
    // time in chosen engine calls (FF9_TRACE_PROFILE): bucket -> ticks, calls
    public static readonly string[] CallNames = { "mesh.Clear", "mesh.vertices", "mesh.colors32", "mesh.uv", "mesh.triangles", "mat.shader", "Camera.main", "GameObject.Find", "rain", "SFX.PostRender", "SFXChannel.Render", "actions.Render", "RenderEventSFX" };
    public static readonly long[] CallTicks = new long[13], CallCount = new long[13];
    public static long T0() => every > 0 ? Stopwatch.GetTimestamp() : 0;
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern unsafe int ff9_sfx_prof(ulong* o, int n);
    public static void Call(int i, long t0) { if (t0 == 0) return; CallTicks[i] += Stopwatch.GetTimestamp() - t0; CallCount[i]++; }
    // the scripts' messages (Update, LateUpdate...) that took the most time
    static readonly Dictionary<MethodInfo, long> scripts = new();
    // the frame's parts: input, fixed steps, starts+Update, coroutines, animation, LateUpdate,
    // render, end-of-frame coroutines, swap, the engine's own ticks, destroys
    static readonly long[] parts = new long[11];
    static readonly string[] partNames = { "input", "fixed", "update", "coroutines", "anim", "late", "render", "endframe", "swap", "ticks", "destroy" };
    public static void Part(int i, ref long since) {
        if (since == 0) return;
        long now = Stopwatch.GetTimestamp();
        parts[i] += now - since;
        since = now;
    }
    public static void Script(MethodInfo m, long ticks) { scripts.TryGetValue(m, out var t); scripts[m] = t + ticks; }

    public static void Frame(long ticksAll) {
        if (every <= 0) return;
        total += ticksAll; worst = Math.Max(worst, ticksAll); frames++;
        if (frames < every) return;
        double ms(long t) => t * 1000.0 / Stopwatch.Frequency / frames;
        Log.Info($"prof parts: gather+sort {ms(Gfx.Pipeline.ProfGather):F2}, bind {ms(Gfx.Pipeline.ProfBind):F2}, draw {ms(Gfx.Pipeline.ProfDraw):F2} ms");
        Gfx.Pipeline.ProfGather = Gfx.Pipeline.ProfBind = Gfx.Pipeline.ProfDraw = 0;
        Log.Info($"prof now: {Gfx.Pipeline.ProfNowCalls / (double)frames:F1} calls, {Gfx.Pipeline.ProfNowVerts / (double)frames:F0} verts per frame; bind {ms(Gfx.Pipeline.ProfNowBind):F2}, upload {ms(Gfx.Pipeline.ProfNowUpload):F2}, draw {ms(Gfx.Pipeline.ProfNowDraw):F2} ms; texture uploads {Gfx.NTexture.ProfUploads / (double)frames:F1}, {ms(Gfx.NTexture.ProfUpload):F2} ms; ReadPixels {Gfx.NTexture.ProfReads / (double)frames:F1}, {ms(Gfx.NTexture.ProfRead):F2} ms");
        unsafe {
            ulong* o = stackalloc ulong[16];
            int k = 0;
            try { k = ff9_sfx_prof(o, 16); } catch (Exception) { }
            if (k >= 16) {
                double ns(ulong v) => v / 1e6 / frames;
                Log.Info($"prof sfx plugin: Update {ns(o[4]):F2}, LateUpdate {ns(o[5]):F2}, UpdateCamera {ns(o[6]):F2}, BeginRender {ns(o[11]):F2}, GetPrim {ns(o[12]):F2} ms; game callbacks {o[14] / (double)frames:F0} taking {ns(o[13]):F2} ms; {o[15] / (double)frames / 1e6:F2} M instructions per frame");
            }
        }
        { var sbc = new System.Text.StringBuilder("prof calls:"); for (int ci = 0; ci < CallNames.Length; ci++) { sbc.Append($" {CallNames[ci]} {CallCount[ci] / (double)frames:F1}x {ms(CallTicks[ci]):F2}"); CallTicks[ci] = CallCount[ci] = 0; } Log.Info(sbc.ToString()); }
        Gfx.Pipeline.ProfNowBind = Gfx.Pipeline.ProfNowUpload = Gfx.Pipeline.ProfNowDraw = Gfx.Pipeline.ProfNowCalls = Gfx.Pipeline.ProfNowVerts = 0;
        Gfx.NTexture.ProfUpload = Gfx.NTexture.ProfUploads = Gfx.NTexture.ProfRead = Gfx.NTexture.ProfReads = 0;
        if (scripts.Count > 0) {
            var top = new List<KeyValuePair<MethodInfo, long>>(scripts);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            Log.Info("prof scripts: " + string.Join(", ", top.GetRange(0, Math.Min(5, top.Count)).ConvertAll(k => $"{k.Key.DeclaringType?.Name}.{k.Key.Name} {ms(k.Value):F2}")));
            scripts.Clear();
        }
        Log.Info("prof frame parts: " + string.Join(", ", partNames.Select((n, i) => $"{n} {ms(parts[i]):F2}")) + $"; behaviours {Player.Behaviours.Count}, waiting to start {Player.PendingStarts}, objects {Registry.Count}; GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, paused {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms, heap {GC.GetTotalMemory(false) >> 20} MB");
        Array.Clear(parts);
        Log.Info($"prof {frames} frames: all {ms(total):F2} ms (logic {ms(total - Render - Present):F2}, render {ms(Render):F2}, swap {ms(Present):F2}), worst {worst * 1000.0 / Stopwatch.Frequency:F1} ms, draws {Gfx.Pipeline.LastDrawCount}, culled {Gfx.Pipeline.LastCulled}");
        total = worst = Render = Present = 0; frames = 0;
    }
}

public static class GpuStats {
    public static int VboUploads, IboUploads, TexUploads, RtCreates;
    public static string LastTex = "";
}
