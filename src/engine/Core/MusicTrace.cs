// FF9_TRACE_MUSIC=1 (tests): every five seconds, the voices Memoria's SoLoud backend is playing
// (resource, valid, paused, volume, stream time, looping and loop point), to see music that stops.
using System;
using System.Collections;
using System.Reflection;

namespace FF9Engine.Core;

public static class MusicTrace {
    static readonly bool on = Environment.GetEnvironmentVariable("FF9_TRACE_MUSIC") != null;
    static readonly bool sfx = Environment.GetEnvironmentVariable("FF9_TRACE_SFX") != null;
    static string lastSfx = "";
    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public static void Tick() {
        if (sfx) SfxTick();
        // not before the title: reading SoundLib runs the game's static set-up early (frame 0
        // made a UIManager of its own, and the real one never got its scenes)
        if (!on || Clock.Frame % 300 != 0 || Player.LastSceneLoaded is not ("Title" or "FieldMap" or "BattleMap" or "WorldMap")) return;
        try {
            var t = Type.GetType("Global.Sound.SoLoud.SdLibAPIWithSoloud, Assembly-CSharp");
            var so = t?.GetField("soloud", F)?.GetValue(null);
            var api = Type.GetType("ISdLibAPIProxy, Assembly-CSharp")?.GetProperty("Instance", F)?.GetValue(null);
            if (so == null || api == null) return;
            var sounds = (IDictionary)t!.GetField("sounds", F)!.GetValue(api)!;
            var streams = (IDictionary)t.GetField("streams", F)!.GetValue(api)!;
            var st = so.GetType();
            object Call(string m, uint h) => st.GetMethod(m, new[] { typeof(uint) })?.Invoke(so, new object[] { h }) ?? "?";
            var line = new System.Text.StringBuilder($"music f{Clock.Frame}: {sounds.Count} voices, active {st.GetMethod("getActiveVoiceCount")?.Invoke(so, null)}");
            foreach (DictionaryEntry e in sounds) {
                uint h = Convert.ToUInt32(e.Key);
                int bank = (int)e.Value!.GetType().GetField("bankID", F)!.GetValue(e.Value)!;
                var info = streams.Contains(bank) ? streams[bank] : null;
                var prof = info?.GetType().GetField("profile", F)?.GetValue(info);
                string res = prof?.GetType().GetField("ResourceID", F)?.GetValue(prof) as string ?? "?";
                line.Append($" | {res} valid={Call("isValidVoiceHandle", h)} pause={Call("getPause", h)} vol={Call("getVolume", h)} t={Call("getStreamTime", h)} loop={Call("getLooping", h)} lp={Call("getLoopPoint", h)}");
            }
            Log.Info(line.ToString());
        } catch (Exception e) { Log.Once("musictrace", "music trace: " + e.Message); }
    }

    // FF9_TRACE_SFX=1 (tests): each frame the voices change, every voice with its stream time
    static void SfxTick() {
        if (Player.LastSceneLoaded is not ("Title" or "FieldMap" or "BattleMap" or "WorldMap")) return;
        try {
            var t = Type.GetType("Global.Sound.SoLoud.SdLibAPIWithSoloud, Assembly-CSharp");
            var so = t?.GetField("soloud", F)?.GetValue(null);
            var api = Type.GetType("ISdLibAPIProxy, Assembly-CSharp")?.GetProperty("Instance", F)?.GetValue(null);
            if (so == null || api == null) return;
            var sounds = (IDictionary)t!.GetField("sounds", F)!.GetValue(api)!;
            var streams = (IDictionary)t.GetField("streams", F)!.GetValue(api)!;
            var st = so.GetType();
            object Call(string m, uint h) => st.GetMethod(m, new[] { typeof(uint) })?.Invoke(so, new object[] { h }) ?? "?";
            var line = new System.Text.StringBuilder();
            foreach (DictionaryEntry e in sounds) {
                uint h = Convert.ToUInt32(e.Key);
                int bank = (int)e.Value!.GetType().GetField("bankID", F)!.GetValue(e.Value)!;
                var info = streams.Contains(bank) ? streams[bank] : null;
                var prof = info?.GetType().GetField("profile", F)?.GetValue(info);
                string res = prof?.GetType().GetField("ResourceID", F)?.GetValue(prof) as string ?? "?";
                if (res.Contains("Music") || res.Contains("music")) continue;
                line.Append($" | {h:x} {res} v={Call("isValidVoiceHandle", h)} p={Call("getPause", h)} vol={Call("getVolume", h)} t={Call("getStreamTime", h):0.000} pos={Call("getStreamPosition", h):0.000}");
            }
            string l = line.ToString();
            if (l != lastSfx) Log.Info($"sfx f{Clock.Frame} active {st.GetMethod("getActiveVoiceCount")?.Invoke(so, null)}:{l}");
            lastSfx = l;
        } catch (Exception e) { Log.Once("sfxtrace", "sfx trace: " + e.Message); }
    }
}
