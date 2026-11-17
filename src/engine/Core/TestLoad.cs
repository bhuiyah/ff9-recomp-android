// FF9_LOAD_FILE=frame:slot:file (tests): at that frame (on the title screen) load a save the way
// the Load Game screen does (FF9StateSystem.Serializer.Load, then SaveLoadUI's finish steps).
// slot and file are 0-based (the screen's "slot 2" / "FILE03" are 1 / 2). Reflection: the game's
// delegate and enum types are built against the old mscorlib.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace FF9Engine.Core;

public static class TestLoad {
    static readonly string? spec = Environment.GetEnvironmentVariable("FF9_LOAD_FILE");
    static bool done;
    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    static Type T(string n) => typeof(FF9StateSystem).Assembly.GetType(n)!;

    // FF9_AUTOLOAD=1 (the launcher's "Play from here" and Start: Continue): Memoria's autosave,
    // loaded from the title screen once it is up. Memoria's own -memoria-autoload loads it while
    // the title skips its intro, then waits for a fade that the skip has taken over, so the
    // screen stayed black.
    static readonly bool autoload = Environment.GetEnvironmentVariable("FF9_AUTOLOAD") == "1";
    static bool autoloadDone;
    static long titleSince = -1;

    // FF9_TEST_SAVE=slot:file (tests): once a field has been up for 300 frames, saves the game there,
    // as a moogle's Save does (FF9StateSystem.Serializer.Save). 0-based, as FF9_LOAD_FILE.
    static readonly string? saveSpec = Environment.GetEnvironmentVariable("FF9_TEST_SAVE");
    static bool saveDone;
    static long fieldSince = -1;

    static void TestSave() {
        if (saveSpec == null || saveDone) return;
        if (Player.LastSceneLoaded != "FieldMap") { fieldSince = -1; return; }
        if (fieldSince < 0) { fieldSince = Clock.Frame; return; }
        if (Clock.Frame - fieldSince < 300) return;
        saveDone = true;
        var p = saveSpec.Split(':');
        int slot = int.Parse(p[0]), file = int.Parse(p[1]);
        try {
            object ser = typeof(FF9StateSystem).GetProperty("Serializer", F)!.GetValue(null)!;
            var save = Array.Find(ser.GetType().GetMethods(F), m => m.Name == "Save" && m.GetParameters().Length == 4)!;
            var finishType = save.GetParameters()[3].ParameterType;
            var ps = Array.ConvertAll(finishType.GetMethod("Invoke")!.GetParameters(), q => Expression.Parameter(q.ParameterType, q.Name));
            var call = Expression.Call(typeof(TestLoad).GetMethod(nameof(Saved), F)!, Expression.Convert(ps[0], typeof(object)), ps[3]);
            save.Invoke(ser, new object?[] { slot, file, null, Expression.Lambda(finishType, call, ps).Compile() });
            Log.Info($"test save: slot {slot} file {file}");
        } catch (Exception e) { Log.Warn("test save failed: " + e); }
    }

    // FF9_TEST_QUADMIST=opponent (tests): once a field has been up for 300 frames, a game of
    // Tetra Master against that opponent, as a card player's event does (SetMiniGame, then the
    // QuadMist scene).
    static readonly string? quadmist = Environment.GetEnvironmentVariable("FF9_TEST_QUADMIST");
    static bool quadmistDone;
    static long qmSince = -1;

    static string? qmState;

    static void TestQuadMist() {
        // the card game's state as it changes (the coin toss, whose turn, ...)
        if (quadmist != null && Player.LastSceneLoaded == "QuadMist" && Clock.Frame % 15 == 0)
            foreach (var b in Player.Behaviours.ToArray())
                if (b.Managed?.GetType() is { Name: "QuadMistGame" } ty) {
                    string st = string.Join(" ", new[] { "StartState", "GameState", "PlayState", "yourTurn" }.Select(n => n + "=" + ty.GetField(n, F)?.GetValue(b.Managed)));
                    if (st != qmState) Log.Info("tetra master: " + st);
                    qmState = st;
                }
        if (quadmist == null || quadmistDone) return;
        if (Player.LastSceneLoaded != "FieldMap") { qmSince = -1; return; }
        if (qmSince < 0) { qmSince = Clock.Frame; return; }
        if (Clock.Frame - qmSince < 300) return;
        quadmistDone = true;
        try {
            T("EventService").GetMethod("SetMiniGame", F)!.Invoke(null, new object[] { ushort.Parse(quadmist) });
            var dir = T("Assets.Scripts.Common.SceneDirector");
            var replace = Array.Find(dir.GetMethods(F), m => m.Name == "Replace" && m.GetParameters().Length == 3)!;
            replace.Invoke(null, new[] { "QuadMist", Enum.ToObject(replace.GetParameters()[1].ParameterType, 1), (object)true });
            Log.Info("test: Tetra Master against " + quadmist);
        } catch (Exception e) { Log.Warn("test quadmist failed: " + e); }
    }

    static void Saved(object err, bool ok) => Log.Info($"test save finished: {err} ok={ok}");

    // FF9_TEST_FIELDS=map,map,...[:frames] (tests, GitHub issue 16): once a field is up, goes to the
    // next map in the list every so many frames (default 240), round and round, as a field exit
    // does (fldMapNo, then SceneDirector.Replace), and logs memory and live objects at each.
    static readonly string? fieldsSpec = Environment.GetEnvironmentVariable("FF9_TEST_FIELDS");
    static long fieldsSince = -1;
    static int fieldsAt, fieldsDone;

    static void TestFields() {
        if (fieldsSpec == null) return;
        if (fieldsSince < 0) {
            if (Player.LastSceneLoaded != "FieldMap") return;
            fieldsSince = Clock.Frame;
            return;
        }
        var p = fieldsSpec.Split(':');
        int every = p.Length > 1 ? int.Parse(p[1]) : 240;
        if (Clock.Frame - fieldsSince < every || Player.LastSceneLoaded != "FieldMap") return;
        Log.Info($"test fields: {fieldsDone} changes, {Memory()}");
        var maps = p[0].Split(',');
        short map = short.Parse(maps[fieldsAt++ % maps.Length]);
        fieldsDone++;
        fieldsSince = Clock.Frame;
        try {
            FF9StateSystem.Common.FF9.fldMapNo = map;
            var sd = T("Assets.Scripts.Common.SceneDirector");
            var replace = Array.Find(sd.GetMethods(F), m => m.Name == "Replace" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(string))!;
            var transition = replace.GetParameters()[1].ParameterType;
            replace.Invoke(null, new object[] { "FieldMap", Enum.ToObject(transition, 0), true });
        } catch (Exception e) { Log.Warn("test fields failed: " + e); }
    }

    /// The process's memory and the engine's live objects, for leak checks.
    static string Memory() {
        string rss = "?";
        try { rss = System.IO.File.ReadAllLines("/proc/self/status").FirstOrDefault(l => l.StartsWith("VmRSS"))?.Substring(6).Trim() ?? "?"; } catch { }
        int tex = 0, mesh = 0; long texMB = 0;
        foreach (var o in Registry.All) {
            if (o is Gfx.NTexture t) { tex++; texMB += (long)t.Width * t.Height * 4; }
            else if (o is Gfx.NMesh) mesh++;
        }
        var names = Registry.All.OfType<Gfx.NTexture>().GroupBy(t => $"{t.Name}:{t.Width}x{t.Height}:{(t.IsAsset ? "a" : "r")}")
            .OrderByDescending(g => g.Count()).Take(6).Select(g => $"{g.Key}x{g.Count()}");
        return $"RSS {rss}, objects {Registry.Count}, textures {tex} ({texMB >> 20} MB at 4 bytes a pixel), meshes {mesh}, managed {GC.GetTotalMemory(false) >> 20} MB; most: {string.Join(" ", names)}";
    }

    public static void Tick() {
        TestFields();
        TestSave();
        TestQuadMist();
        if (autoload && !autoloadDone) {
            if (Player.LastSceneLoaded != "Title") titleSince = -1;
            else if (titleSince < 0) titleSince = Clock.Frame;
            else if (Clock.Frame - titleSince >= 120) {
                autoloadDone = true;
                try { LoadAutosaveNow(); } catch (Exception e) { Log.Warn("autoload failed: " + e); }
            }
        }
        if (spec == null || done) return;
        var p = spec.Split(':');
        if (p.Length < 3 || !int.TryParse(p[0], out int at) || Clock.Frame < at) return;
        // on the title screen (the app reaches it later than the headless host)
        if (Player.LastSceneLoaded != "Title") return;
        done = true;
        int slot = int.Parse(p[1]), file = int.Parse(p[2]);
        Log.Info($"test load: slot {slot} file {file}");
        try {
            object ser = typeof(FF9StateSystem).GetProperty("Serializer", F)!.GetValue(null)!;
            var load = Array.Find(ser.GetType().GetMethods(F), m => m.Name == "Load" && m.GetParameters().Length == 4)!;
            var finishType = load.GetParameters()[3].ParameterType;
            var ps = Array.ConvertAll(finishType.GetMethod("Invoke")!.GetParameters(), q => Expression.Parameter(q.ParameterType, q.Name));
            var call = Expression.Call(typeof(TestLoad).GetMethod(nameof(Finished), F)!,
                Expression.Convert(ps[0], typeof(object)), ps[1], ps[2], ps[3]);
            var del = Expression.Lambda(finishType, call, ps).Compile();
            load.Invoke(ser, new object?[] { slot, file, null, del });
        } catch (Exception e) { Log.Warn("test load failed: " + e); }
    }

    /// The panels' Load: Memoria's autosave (a save state put there by the app) loaded now, in game.
    public static void LoadAutosaveNow() {
        object ser = typeof(FF9StateSystem).GetProperty("Serializer", F)!.GetValue(null)!;
        var load = Array.Find(ser.GetType().GetMethods(F), m => m.Name == "Autoload" && m.GetParameters().Length == 2)!;
        var finishType = load.GetParameters()[1].ParameterType;
        var ps = Array.ConvertAll(finishType.GetMethod("Invoke")!.GetParameters(), q => Expression.Parameter(q.ParameterType, q.Name));
        var call = Expression.Call(typeof(TestLoad).GetMethod(nameof(AutoloadFinished), F)!, Expression.Convert(ps[0], typeof(object)), ps[1]);
        load.Invoke(ser, new object?[] { null, Expression.Lambda(finishType, call, ps).Compile() });
        Log.Info("panel: loading the autosave");
    }

    static void AutoloadFinished(object err, bool ok) => Finished(err, 0, 0, ok);

    static void Finished(object err, int slot, int file, bool ok) {
        Log.Info($"test load finished: {err} ok={ok}");
        if (!ok || err.ToString() != "Success") return;
        try {
            T("SoundLib").GetMethod("StopMovieMusic", F)?.Invoke(null, new object[] { "FMV000", true });
            var settings = typeof(FF9StateSystem).GetProperty("Settings", F)!.GetValue(null)!;
            settings.GetType().GetMethod("SetSound", F, Type.EmptyTypes)?.Invoke(settings, null);
            settings.GetType().GetMethod("SetSoundEffect", F, Type.EmptyTypes)?.Invoke(settings, null);
            var ee = typeof(PersistenSingleton<>).MakeGenericType(T("EventEngine")).GetProperty("Instance")!.GetValue(null)!;
            ee.GetType().GetMethod("ReplaceLoadMap", F)!.Invoke(ee, null);
            var ui = typeof(PersistenSingleton<>).MakeGenericType(T("UIManager")).GetProperty("Instance")!.GetValue(null)!;
            var title = ui.GetType().GetProperty("TitleScene", F)?.GetValue(ui);
            title?.GetType().GetProperty("SplashScreenEnabled", F)?.SetValue(title, true);
            settings.GetType().GetMethod("UpdateSetting", F, Type.EmptyTypes)?.Invoke(settings, null);
            T("ff9").GetMethod("w_frameNewSession", F)?.Invoke(null, null);
        } catch (Exception e) { Log.Warn("test load finish failed: " + e); }
    }
}
