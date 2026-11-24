// Debug: FF9_DUMP_FRAME=n prints the active scene hierarchy (components, enabled/started) at frame n.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FF9Engine.Core;

public static class DebugDump {
    static readonly int[] frames = (Environment.GetEnvironmentVariable("FF9_DUMP_FRAME") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
    static readonly string? goFilter = Environment.GetEnvironmentVariable("FF9_INSPECT_GO")?.Replace('_', ' ');

    static readonly bool probe = Environment.GetEnvironmentVariable("FF9_PROBE") != null;
    static readonly int probeAt = int.TryParse(Environment.GetEnvironmentVariable("FF9_PROBE"), out var pa) ? pa : -1;

    static void Try(string what, Func<object?> f) {
        try { Console.WriteLine($"probe {what} = {f() ?? "null"}"); }
        catch (Exception e) { Console.WriteLine($"probe {what} THROWS {e.GetType().Name}: {e.Message}"); }
    }

    /// FF9_PROBE: evaluates the calls FieldMapActorController.MovePC makes, one by one.
    static void Probe() {
        try {
            var sys = typeof(HonoBehaviorSystem);
            var list = sys.GetField("_behaviorList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
            object? inst = list!.IsStatic ? null : HonoBehaviorSystem.Instance;
            var l = (System.Collections.IList)list.GetValue(inst)!;
            var names = new List<string>();
            foreach (var o in l) {
                string n = o?.GetType().Name ?? "null";
                if (o is UnityEngine.Object uo && (n == "FieldMap" || n == "FieldSPSSystem" || n == "FieldMapActorController" || n == "HonoluluFieldMain"))
                    n += $"#{Registry.Get(uo)?.Id}({(Registry.Get(uo) as NComponent)?.GameObject.Name}{((Registry.Get(uo) as NMonoBehaviour)?.Awoken == true ? ",awoken" : "")})";
                if (n.StartsWith("HonoEventObject")) continue;
                names.Add(n);
            }
            Console.WriteLine($"probe behaviorList ({l.Count}): " + string.Join(", ", names));
            var mc = typeof(EventInput).GetField("isMovementControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Console.WriteLine($"probe isMovementControl = {mc?.GetValue(null)}");
            var pm = (uint[]?)typeof(EventInput).GetField("PSXCntlPadMask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
            var mm = typeof(EventInput).GetField("MovementMask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null);
            Console.WriteLine($"probe padmask = {(pm == null ? "null" : string.Join(",", pm.Select(x => x.ToString("x"))))} movementMask = {mm:x} fldMapNo = {FF9StateSystem.Common.FF9.fldMapNo} userControl = {PersistenSingleton<EventEngine>.Instance.GetUserControl()}");
            foreach (var b in Player.Behaviours.ToList())
                if (b.Managed is FieldMapActorController fc) {
                    var oa = fc.originalActor;
                    var st = typeof(EventEngine).GetField("stateRunning", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null);
                    Console.WriteLine($"probe controller on '{b.GameObject.Name}' isPlayer={fc.isPlayer} actorState={oa?.state} stateRunning={st} fieldMap={(fc.fieldMap == null ? "null" : "set")}");
                }
        } catch (Exception e) { Console.WriteLine("probe list failed: " + e.Message); }
        Try("Field", () => FF9StateSystem.Field);
        Try("Field.isDebug", () => FF9StateSystem.Field.isDebug);
        Try("IsMovementControl", () => EventInput.IsMovementControl);
        Try("HonoInputManager", () => PersistenSingleton<HonoInputManager>.Instance);
        Try("GetAxis", () => PersistenSingleton<HonoInputManager>.Instance.GetAxis());
        Try("UIManager.Input", () => UIManager.Input);
        Try("XInputManager", () => PersistenSingleton<UnityXInput.XInputManager>.Instance);
        Try("CurrentState", () => { var x = PersistenSingleton<UnityXInput.XInputManager>.Instance; var st = x.GetType().GetProperty("CurrentState")!.GetValue(x)!; var dp = st.GetType().GetProperty("DPad")!.GetValue(st)!; return dp.GetType().GetProperty("Down")!.GetValue(dp); });
        Try("GetKey", () => { var inp = UIManager.Input; var m = inp.GetType().GetMethod("GetKey")!; return m.Invoke(inp, new[] { Enum.ToObject(m.GetParameters()[0].ParameterType, 0) }); });
        Try("EventEngine", () => PersistenSingleton<EventEngine>.Instance);
        Try("GetUserControl", () => PersistenSingleton<EventEngine>.Instance.GetUserControl());
    }

    static bool unitsProbed;
    static readonly bool probeBattle = Environment.GetEnvironmentVariable("FF9_PROBE_BTL") != null;

    static string GoName(object? o) => o is UnityEngine.Object u && Registry.Get(u) is { } n ? $"'{n.Name}'#{n.Id}" : (o == null ? "null" : "destroyed");

    /// FF9_PROBE_BTL: battle menu state every 30 frames (which button the confirm key goes to).
    static void ProbeBattle() {
        string stage = "find";
        try {
            BattleHUD? hud = null;
            foreach (var b in Player.Behaviours.ToList()) if (b.Managed is BattleHUD h) { hud = h; break; }
            if (hud == null) return;
            stage = "units";
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
            object? Get(object o, string f) => o.GetType().GetField(f, F)?.GetValue(o) ?? o.GetType().GetProperty(f, F)?.GetValue(o);
            if (!unitsProbed) {
                object? battle = typeof(FF9StateSystem).GetProperty("Battle")?.GetValue(null);
                object? ff9b = battle == null ? null : Get(battle, "FF9Battle");
                object? d = ff9b == null ? null : Get(Get(ff9b, "btl_list")!, "next");
                for (; d != null; d = Get(d, "next")) {
                    var go = Get(d, "gameObject") as UnityEngine.GameObject;
                    if (go == null) { Console.WriteLine("probe unit go=null"); continue; }
                    string bone = "bone" + Convert.ToInt32(Get(d, "tar_bone")).ToString("D3");
                    int n = 0;
                    try { foreach (var c in (System.Collections.IEnumerable)go.transform) n++; } catch (Exception e) { Console.WriteLine("probe unit enum THROWS " + e); }
                    UnityEngine.Transform? found = null;
                    try { found = go.transform.GetChildByName(bone); } catch (Exception e) { Console.WriteLine("probe unit find THROWS " + e); }
                    unitsProbed = true;
                    Console.WriteLine($"probe unit go={GoName(go)} children={n} childCount={go.transform.childCount} {bone} found={GoName(found)}");
                    var anim = go.GetComponent<UnityEngine.Animation>();
                    string? cur = Get(d, "currentAnimationName") as string;
                    try {
                        var st = anim == null || cur == null ? null : anim[cur];
                        int ns = 0, nnull = 0;
                        if (anim != null) foreach (var s0 in (System.Collections.IEnumerable)anim) { ns++; if (s0 == null) nnull++; }
                        Console.WriteLine($"probe unit anim={(anim == null ? "null" : "ok")} cur='{cur}' state={(st == null ? "null" : st.name + " t=" + st.time + " len=" + st.length)} states={ns} nullStates={nnull}");
                    } catch (Exception e) { Console.WriteLine("probe unit anim THROWS " + e); }
                }
            }
            if (hud.CurrentPlayerIndex < 0 && string.IsNullOrEmpty(ButtonGroupState.ActiveGroup)) return;
            stage = "fields";
            var tp = Get(hud, "_targetPanel");
            string en = "?";
            if (tp != null && Get(tp, "Enemies") is System.Collections.IEnumerable enemies) {
                var parts = new List<string>();
                foreach (var e in enemies) parts.Add(e == null ? "null" : GoName(Get(e, "GameObject")));
                en = string.Join(",", parts);
            }
            var ml = Get(hud, "_matchBattleIdEnemyList") is System.Collections.ICollection mc ? mc.Count : -1;
            stage = "group";
            string group = ButtonGroupState.ActiveGroup;
            stage = "active";
            string active = GoName(ButtonGroupState.ActiveButton), selected = GoName(UICamera.selectedObject);
            stage = "print";
            Console.WriteLine($"probe btl f{Clock.Frame} group='{group}' active={active} selected={selected} cursor={Get(hud, "_cursorType")} player={hud.CurrentPlayerIndex} matchEnemies={ml} enemies=[{en}]");
        } catch (Exception e) { Console.WriteLine($"probe btl failed at {stage}: " + e); }
    }


    static readonly bool traceList = Environment.GetEnvironmentVariable("FF9_TRACE_LIST") != null;
    static readonly Dictionary<RecycleListPopulator, string> listLast = new();
    /// FF9_TRACE_LIST: cursor row, scroll and spring of the active lists, each frame they change.
    static void TraceList() {
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
        foreach (var b in Player.Behaviours.ToList()) {
            if (b.Managed is not RecycleListPopulator l || !b.GameObject.ActiveInHierarchy || l.panel == null) continue;
            try {
                int act = (int)typeof(RecycleListPopulator).GetField("activeNumber", F)!.GetValue(l)!;
                var sel = UICamera.selectedObject;
                string row = "-", vis = "-";
                var it = sel == null ? null : sel.GetComponent<RecycleListItem>();
                if (it != null) {
                    row = it.ItemDataIndex + (sel!.activeInHierarchy ? "" : "(off)");
                    float y = l.panel.transform.InverseTransformPoint(sel.transform.position).y;
                    var c = l.panel.finalClipRegion;
                    vis = $"{(Math.Abs(y - c.y) < c.w / 2 ? "IN" : "OUT")} y={y:F0} clip={c.y:F0}/{c.w:F0}";
                }
                var sp = l.panel.GetComponent<SpringPanel>();
                string st = $"{GoName(l.gameObject)} act={act} row={row} {vis} group={ButtonGroupState.ActiveGroup} spring={(sp != null && sp.enabled ? sp.target.y.ToString("F0") : "off")}";
                if (listLast.TryGetValue(l, out var o) && o == st) continue;
                listLast[l] = st;
                Console.WriteLine($"trace list f{Clock.Frame} {st}");
            } catch (Exception e) { Console.WriteLine("trace list: " + e.Message); }
        }
    }

    static readonly int nameTestAt = int.TryParse(Environment.GetEnvironmentVariable("FF9_NAMETEST"), out var nt) ? nt : -1;

    /// FF9_NAMETEST=frame: reads every live object's name through UnityEngine.Object.name and checks it.
    static void NameTest() {
        int n = 0, bad = 0;
        foreach (var o in Registry.All.ToList()) {
            if (o?.Managed is not UnityEngine.Object u) continue;
            n++;
            string s = u.name;
            bool ok = s != null && s.Length < 4096;
            if (ok) foreach (char ch in s!) if (ch < ' ' && ch != '\t') { ok = false; break; }
            if (!ok && bad++ < 10) Console.WriteLine($"nametest BAD {o.GetType().Name}#{o.Id} len={s?.Length}");
        }
        Console.WriteLine($"nametest: {n} objects, {bad} bad; GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}");
        GC.Collect();
        bad = 0;
        foreach (var o in Registry.All.ToList()) {
            if (o?.Managed is not UnityEngine.Object u) continue;
            string s = u.name;
            if ((s == null || s.Length > 4096) && bad++ < 10) Console.WriteLine($"nametest after GC BAD {o.GetType().Name}#{o.Id} engine='{o.Name}' got={(s == null ? "null" : "len " + s.Length)} same={ReferenceEquals(s, o.Name)}");
            else if (bad < 3 && s != null) Console.WriteLine($"nametest after GC ok? '{s}' engine='{o.Name}'");
        }
        Console.WriteLine($"nametest after GC: {bad} bad");
    }

    // FF9_WARP_WORLD=frame:wldMapNo (tests): at that frame in a field, switch to the world map the way
    // the field's own loop does (nextMode 3 + a scene-change flag)
    static readonly string? warpWorld = Environment.GetEnvironmentVariable("FF9_WARP_WORLD");

    static void WarpWorld() {
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
        object? Get(object? o, string n) => o == null ? null : (o.GetType().GetField(n, F)?.GetValue(o) ?? o.GetType().GetProperty(n, F)?.GetValue(o));
        void Set(object? o, string n, object v) {
            if (o == null) return;
            var f = o.GetType().GetField(n, F);
            if (f != null) { f.SetValue(o, Convert.ChangeType(v, f.FieldType)); return; }
            var pr = o.GetType().GetProperty(n, F);
            if (pr != null && pr.CanWrite) pr.SetValue(o, Convert.ChangeType(v, pr.PropertyType));
        }
        var parts = warpWorld!.Split(':');
        object? ff9 = typeof(FF9StateSystem).GetProperty("Common")?.GetValue(null);
        ff9 = Get(ff9, "FF9");
        object? field = Get(Get(Get(typeof(FF9StateSystem).GetProperty("Field")?.GetValue(null), "FF9Field"), "loc"), "map");
        if (parts.Length > 1) Set(ff9, "wldMapNo", short.Parse(parts[1]));
        Set(field, "nextMode", 3);
        var sys = typeof(PersistenSingleton<>).MakeGenericType(typeof(FF9StateSystem)).GetProperty("Instance")?.GetValue(null);
        Set(sys, "attr", Convert.ToUInt32(Get(sys, "attr")) | 1u);
        Console.WriteLine($"warp: world map {Get(ff9, "wldMapNo")}, field nextMode {Get(field, "nextMode")}");
    }

    // FF9_IL_DUMP=Type::Method:fromHex:toHex (debugging): the method's IL in that range, with
    // tokens resolved, printed once at start
    public static void IlDump() {
        var spec = Environment.GetEnvironmentVariable("FF9_IL_DUMP");
        if (spec == null) return;
        try {
            var parts = spec.Split(':');
            var t = typeof(FF9StateSystem).Assembly.GetType(parts[0]);
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
            var m = Array.Find(t!.GetMethods(flags), x => x.Name == parts[2]);
            byte[] il = m!.GetMethodBody()!.GetILAsByteArray()!;
            int from = Convert.ToInt32(parts[3], 16), to = Convert.ToInt32(parts[4], 16);
            var one = new System.Collections.Generic.Dictionary<short, System.Reflection.Emit.OpCode>();
            foreach (var f in typeof(System.Reflection.Emit.OpCodes).GetFields()) { var op = (System.Reflection.Emit.OpCode)f.GetValue(null)!; one[op.Value] = op; }
            int i = 0;
            while (i < il.Length) {
                int at = i; short v = il[i++];
                if (v == 0xFE) v = (short)(0xFE00 | il[i++]);
                var op = one[v];
                int sz = op.OperandType switch {
                    System.Reflection.Emit.OperandType.InlineNone => 0,
                    System.Reflection.Emit.OperandType.ShortInlineBrTarget or System.Reflection.Emit.OperandType.ShortInlineI or System.Reflection.Emit.OperandType.ShortInlineVar => 1,
                    System.Reflection.Emit.OperandType.InlineVar => 2,
                    System.Reflection.Emit.OperandType.InlineI8 or System.Reflection.Emit.OperandType.InlineR => 8,
                    System.Reflection.Emit.OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                    _ => 4 };
                string arg = "";
                if (op.OperandType is System.Reflection.Emit.OperandType.InlineField or System.Reflection.Emit.OperandType.InlineMethod or System.Reflection.Emit.OperandType.InlineType or System.Reflection.Emit.OperandType.InlineTok)
                    try { var mem = m.Module.ResolveMember(BitConverter.ToInt32(il, i), t.IsGenericType ? t.GetGenericArguments() : null, null); arg = mem!.DeclaringType?.Name + "::" + mem.Name; } catch (Exception e) { arg = "?" + e.GetType().Name; }
                else if (sz == 1) arg = il[i].ToString();
                else if (sz == 4 && op.OperandType != System.Reflection.Emit.OperandType.InlineSwitch) arg = BitConverter.ToInt32(il, i).ToString();
                i += sz;
                if (at >= from && at <= to) Console.WriteLine($"il IL_{at:x4} {op.Name} {arg}");
            }
        } catch (Exception e) { Console.WriteLine("il dump failed: " + e); }
    }

    static readonly bool saveUiProbe = Environment.GetEnvironmentVariable("FF9_TEST_SAVEUI") != null;
    static string lastSaveUi = "";

    /// FF9_TEST_SAVEUI=1: the Save/Load screen's state, when it changes.
    static void SaveUi() {
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
        try {
            var asm = typeof(FF9StateSystem).Assembly;
            var ui = typeof(PersistenSingleton<>).MakeGenericType(asm.GetType("UIManager")!).GetProperty("Instance")!.GetValue(null)!;
            object? scene = ui.GetType().GetField("SaveLoadScene", F)?.GetValue(ui);
            var bgs = asm.GetType("ButtonGroupState")!;
            string group = bgs.GetProperty("ActiveGroup", F)?.GetValue(null) as string ?? "null";
            var btn = bgs.GetProperty("ActiveButton", F)?.GetValue(null) as UnityEngine.GameObject;
            string s = scene == null ? "no scene" : $"active={((UnityEngine.Behaviour)scene).isActiveAndEnabled} loading={asm.GetType("UIScene")!.GetField("isLoading", F)!.GetValue(scene)} group={group} button={btn?.name}";
            var prev = scene?.GetType().GetField("LoadingPreviewDialog", F)?.GetValue(scene) as UnityEngine.GameObject;
            s += $" previewDialog={prev?.activeSelf} uiState={ui.GetType().GetProperty("State", F)?.GetValue(ui)}";
            if (scene != null && asm.GetType("UIScene")!.GetProperty("FadingComponent", F)?.GetValue(scene) is UnityEngine.Component fade)
                s += $" fade: active={fade.gameObject.activeInHierarchy} busy={fade.GetType().GetField("busy", F)?.GetValue(fade)}";
            if (s != lastSaveUi) { lastSaveUi = s; Log.Info("save ui: " + s); }
        } catch (Exception e) { Log.Info("save ui probe: " + e.Message); }
    }

    static readonly bool drawCallProbe = Environment.GetEnvironmentVariable("FF9_TEST_DRAWCALLS") != null;
    static string lastDrawCalls = "";

    /// FF9_TEST_DRAWCALLS=1: NGUI's draw calls (panel, clipping, shader, clip count, material's
    /// clip range), when they change.
    static void DrawCalls() {
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
        try {
            var t = typeof(FF9StateSystem).Assembly.GetType("UIDrawCall")!;
            var list = t.GetField("mActiveList", F)!.GetValue(null)!;
            int n = (int)list.GetType().GetField("size")!.GetValue(list)!;
            var arr = (Array)list.GetType().GetField("buffer")!.GetValue(list)!;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++) {
                var dc = arr.GetValue(i)!;
                var panel = t.GetField("panel", F)!.GetValue(dc) as UnityEngine.Component;
                var mat = t.GetField("mDynamicMat", F)!.GetValue(dc) as UnityEngine.Material;
                var tex = t.GetField("mTexture", F)!.GetValue(dc) as UnityEngine.Texture;
                object? clipping = panel?.GetType().GetProperty("clipping", F)?.GetValue(panel);
                string cr = mat == null ? "-" : $"{mat.GetVector("_ClipRange0")} args0={mat.GetVector("_ClipArgs0")} r1={mat.GetVector("_ClipRange1")} a1={mat.GetVector("_ClipArgs1")} r2={mat.GetVector("_ClipRange2")} a2={mat.GetVector("_ClipArgs2")}";
                sb.Append($"\n  dc{i} panel={panel?.name}({clipping}) shader={mat?.shader?.name} clips={t.GetField("mClipCount", F)!.GetValue(dc)} tex={tex?.name} clip0={cr} tris={t.GetField("mTriangles", F)!.GetValue(dc)}");
            }
            string s = sb.ToString();
            if (s != lastDrawCalls) { lastDrawCalls = s; Log.Info($"draw calls at frame {Clock.Frame}:" + s); }
        } catch (Exception e) { Log.Info("draw call probe: " + e.Message); }
    }

    public static void Tick() {
        if (drawCallProbe) DrawCalls();
        if (saveUiProbe && Clock.Frame % 10 == 0) SaveUi();
        if (Clock.Frame == 2) IlDump();
        if (warpWorld != null && int.TryParse(warpWorld.Split(':')[0], out int wf) && Clock.Frame == wf)
            try { WarpWorld(); } catch (Exception e) { Console.WriteLine("warp failed: " + e); }
        if (Clock.Frame == nameTestAt) NameTest();
        if (traceList) TraceList();
        if (probeBattle && Clock.Frame % 5 == 0) ProbeBattle();
        if (probe && (Clock.Frame % 500 == 250 || Clock.Frame == probeAt)) Probe();
        if (!frames.Contains(Clock.Frame)) return;
        Console.WriteLine($"==== frame {Clock.Frame} t={Clock.Now:F2}");
        if (Environment.GetEnvironmentVariable("FF9_DUMP_RT") is { } rtName)
            foreach (var o in Registry.All.ToList())
                if (o is Gfx.NRenderTexture rt && rt.Created && rt.Name.Contains(rtName)) unsafe {
                    var px = new byte[rt.Width * rt.Height * 4];
                    Gfx.GL.glBindFramebuffer(Gfx.GL.FRAMEBUFFER, rt.Fbo);
                    Gfx.GL.glPixelStorei(Gfx.GL.PACK_ALIGNMENT, 1);
                    fixed (byte* p = px) Gfx.GL.glReadPixels(0, 0, rt.Width, rt.Height, Gfx.GL.RGBA, Gfx.GL.UNSIGNED_BYTE, p);
                    Gfx.GL.glBindFramebuffer(Gfx.GL.FRAMEBUFFER, Gfx.RenderTargets.CurrentFbo);
                    Gfx.Png.Write($"/data/local/tmp/ff9/shots/rt_{rt.Id}.png", px, rt.Width, rt.Height, flipY: true);
                    Console.WriteLine($"dumped render texture #{rt.Id} '{rt.Name}'");
                }
        if (goFilter != null) {
            foreach (var mb in Player.Behaviours.ToList())
                if (mb.Managed != null && mb.GameObject.Name == goFilter && mb.GameObject.ActiveInHierarchy) Inspect(mb);
            return;
        }
        var sb = new StringBuilder();
        foreach (var o in Registry.All.OfType<NGameObject>().Where(g => !g.IsAsset && g.Transform.Parent == null).ToList())
            Dump(o.Transform, sb, 0);
        Console.WriteLine(sb.ToString());
        if (Environment.GetEnvironmentVariable("FF9_INSPECT") is { } types)
            foreach (var mb in Player.Behaviours.ToList())
                if (mb.Managed != null && types.Split(',').Contains(mb.Managed.GetType().Name)) Inspect(mb);
    }

    static void Inspect(NMonoBehaviour mb) {
        var o = mb.Managed!;
        Console.WriteLine($"inspect {o.GetType().Name} on '{mb.GameObject.Name}' enabled={mb.Enabled} started={mb.Started}");
        for (var t = o.GetType(); t != null && t != typeof(UnityEngine.MonoBehaviour); t = t.BaseType)
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly)) {
                object? v;
                try { v = f.GetValue(o); } catch { v = "?"; }
                string s = v switch {
                    null => "null",
                    UnityEngine.AnimationCurve c => "curve[" + string.Join(" ", c.keys.Select(k => $"({k.time},{k.value})")) + $"] e(0.5)={c.Evaluate(0.5f)}",
                    UnityEngine.Object u => Registry.Get(u) is { } n ? $"{u.GetType().Name} '{n.Name}'" : "destroyed",
                    _ => v.ToString() ?? "",
                };
                Console.WriteLine($"   {t.Name}.{f.Name} = {s}");
            }
    }

    static void Dump(NTransform t, StringBuilder sb, int depth) {
        var g = t.GameObject;
        if (!g.ActiveInHierarchy && depth > 0) return;
        sb.Append(' ', depth * 2).Append(g.Name).Append(g.ActiveSelf ? "" : " (inactive)").Append(" L").Append(g.Layer).Append(" pos=").Append(t.LocalPosition).Append(" :");
        foreach (var c in g.Components.Skip(1)) {
            string n = c.Managed?.GetType().Name ?? "?";
            sb.Append(' ').Append(n);
            if (!c.Enabled) sb.Append("(off)");
            else if (c is NMonoBehaviour mb && !mb.Started) sb.Append("(nostart)");
        }
        sb.Append('\n');
        foreach (var ch in t.Children) Dump(ch, sb, depth + 1);
    }
}
