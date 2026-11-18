// FF9_TRACE_PROFILE only: BattleMapCameraController.OnPostRender is replaced by the same steps
// (rain, SFX.PostRender, UnifiedBattleSequencer.LoopRender split into SFXChannel.Render, each running
// action's Render and SFXData.RenderEventSFX), each timed into Prof's call buckets, to see where a
// heavy battle effect's frame goes. If a step isn't found (another Memoria), nothing is replaced.
using System;
using System.Collections;
using System.Reflection;
using FF9Engine.Core;

namespace FF9Engine.Native;

static class ProfPostRender {
    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static FieldInfo? rainField, actionsField;
    static MethodInfo? rain, sfxPost, channel, actionRender, eventSfx;

    public static void Install() {
        if (!Prof.On) return;
        var bmcc = Type.GetType("BattleMapCameraController, Assembly-CSharp");
        var target = bmcc?.GetMethod("OnPostRender", F, null, Type.EmptyTypes, null);
        rainField = bmcc?.GetField("rainRenderer", F);
        rain = rainField?.FieldType.GetMethod("nf_BbgRain", F, null, Type.EmptyTypes, null);
        sfxPost = Type.GetType("SFX, Assembly-CSharp")?.GetMethod("PostRender", F, null, Type.EmptyTypes, null);
        var ubs = Type.GetType("UnifiedBattleSequencer, Assembly-CSharp");
        actionsField = ubs?.GetField("runningActions", F);
        actionRender = ubs?.GetNestedType("BattleAction", F)?.GetMethod("Render", F, null, Type.EmptyTypes, null);
        channel = Type.GetType("SFXChannel, Assembly-CSharp")?.GetMethod("Render", F, null, Type.EmptyTypes, null);
        eventSfx = Type.GetType("SFXData, Assembly-CSharp")?.GetMethod("RenderEventSFX", F, null, Type.EmptyTypes, null);
        if (target == null || rain == null || sfxPost == null || actionsField == null || actionRender == null || channel == null || eventSfx == null) {
            Log.Info("prof: OnPostRender steps not found, not split");
            return;
        }
        Compat.Detour(target, typeof(ProfPostRender).GetMethod(nameof(OnPostRender), F)!);
        Log.Info("prof: OnPostRender split into its steps");
    }

    static void OnPostRender(object self) {
        long t = Prof.T0(); rain!.Invoke(rainField!.GetValue(self), null); Prof.Call(8, t);
        t = Prof.T0(); sfxPost!.Invoke(null, null); Prof.Call(9, t);
        t = Prof.T0(); channel!.Invoke(null, null); Prof.Call(10, t);
        t = Prof.T0();
        if (actionsField!.GetValue(null) is IList list) for (int i = 0; i < list.Count; i++) actionRender!.Invoke(list[i], null);
        Prof.Call(11, t);
        t = Prof.T0(); eventSfx!.Invoke(null, null); Prof.Call(12, t);
    }
}
