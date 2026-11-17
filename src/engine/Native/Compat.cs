// .NET Framework (Unity 5.2's Mono) behaviour the game relies on that .NET 8 changed: the methods
// are patched at their compiled entry to jump to replacements here.
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace FF9Engine.Native;

public static class Compat {
    [DllImport("__Internal")] static extern int ff9_jump_patch(IntPtr from, IntPtr to);

    public static void Detour(MethodBase target, MethodInfo replacement) {
        RuntimeHelpers.PrepareMethod(target.MethodHandle);
        RuntimeHelpers.PrepareMethod(replacement.MethodHandle);
        IntPtr from = target.MethodHandle.GetFunctionPointer(), to = replacement.MethodHandle.GetFunctionPointer();
        if (ff9_jump_patch(from, to) != 0) Log.Warn($"detour of {target.DeclaringType?.Name}.{target.Name} failed");
    }

    static MethodInfo M(string name) => typeof(Compat).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    public static void Install() {
        Detour(typeof(Assembly).GetMethod(nameof(Assembly.LoadFile), new[] { typeof(string) })!, M(nameof(LoadFile)));
        InstallThreadStart();
        InstallCloudFolder();
        InstallMovieClock();
        InstallNoWalkmeshFix();
    }

    // Movies with another frame rate than the game's 15 (Moguri's 30 fps ones). MovieMaterial.Open
    // sets playSpeed = 15 / the movie's rate, right for its fixed step (1/15 s a frame, FMVs with
    // Memoria's VSync off) but not for its other one, the frame's time (VSync on, the default, and
    // all field movies): the movie's clock ran at half speed and only the jumps to the sound's
    // position (when it was 66 ms behind) moved it on: drawn at the movie's rate (MBG.Play sets the
    // frame rate to it), the picture went slow, slow, jump (GitHub issue 27). MovieMaterial.Load (which calls Open) is replaced by
    // the same steps, and then a clock that follows the frame's time runs at speed 1.
    static FieldInfo? mmHasStream, mmFirstFrame, mmCycle, mmKey, mmElapsed, mmAdvance, mmFinished, mmSpeed, mmIsFmv;
    static MethodInfo? mmStop, mmOpen;
    static PropertyInfo? mmVSync;

    static void InstallMovieClock() {
        const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var t = Type.GetType("Assets.Sources.Graphics.Movie.MovieMaterial, Assembly-CSharp");
        FieldInfo? F(string n) => t?.GetField(n, I);
        mmHasStream = F("hasStream"); mmFirstFrame = F("getFirstFrame"); mmCycle = F("preciseTimeCycleCounter");
        mmKey = F("movieKey"); mmElapsed = F("m_elapsedTime"); mmAdvance = F("advance");
        mmFinished = F("m_hasFinished"); mmSpeed = F("playSpeed"); mmIsFmv = F("isFMV");
        mmStop = t?.GetMethod("Stop", I, null, Type.EmptyTypes, null);
        mmOpen = t?.GetMethod("Open", I, null, Type.EmptyTypes, null);
        mmVSync = Type.GetType("Memoria.Configuration+Graphics, Assembly-CSharp")?.GetProperty("VSync", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var load = t?.GetMethod("Load", I, null, new[] { typeof(string) }, null);
        if (load == null || mmHasStream == null || mmFirstFrame == null || mmCycle == null || mmKey == null || mmElapsed == null
            || mmAdvance == null || mmFinished == null || mmSpeed == null || mmIsFmv == null || mmStop == null || mmOpen == null || mmVSync == null) {
            if (t != null) Log.Warn("movies: this Memoria's MovieMaterial differs; its clock is left as it is");
            return;
        }
        Detour(load, M(nameof(MovieLoad)));
    }

    static void MovieLoad(object self, string movieKey) {
        if ((bool)mmHasStream!.GetValue(self)!) Invoke(mmStop!, self);
        mmFirstFrame!.SetValue(self, false);
        mmCycle!.SetValue(self, 0);
        mmKey!.SetValue(self, movieKey);
        mmElapsed!.SetValue(self, 0.0);
        try { Invoke(mmOpen!, self); }
        catch (Exception e) { Log.Warn($"[Movie.MovieMaterial.GLPlugin] Error when loading {movieKey}: {e}"); throw; }
        mmAdvance!.SetValue(self, false);
        mmHasStream.SetValue(self, true);
        mmFinished!.SetValue(self, false);
        bool fixedStep = (bool)mmIsFmv!.GetValue(self)! && !(bool)mmVSync!.GetValue(null)!;
        if (!fixedStep) mmSpeed!.SetValue(self, 1f);
    }

    // The opening movie's field (map 70, "Opening-For FMV") has no walkmesh: FieldMap.LoadFieldMap
    // stops before making one. With Memoria's PSXMovementMethod on, FieldMapActorController.MovePC
    // still reads fieldMap.walkMesh for the slope, and the null threw in every frame. The throw left
    // HonoBehaviorSystem's list of new behaviours uncleared, so they were added to its main list
    // again each frame (about 40 more a frame): the game got slower through the whole movie, 30 fps
    // to 15 by its end (GitHub issue 27). While the field has no walkmesh, PSXMovementMethod
    // (the slope, which needs one) is off here; it comes back as it was when there is one.
    static FieldInfo? evInstance, evFieldmap, fmWalkMesh, cfgInstance, cfgControl, ctlPsxMove, iniValue;
    static bool walkFixReady, psxOff;
    static object? psxIni;

    static void InstallNoWalkmeshFix() {
        const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var asm = Type.GetType("FieldMap, Assembly-CSharp")?.Assembly;
        if (asm == null) return;
        var ev = asm.GetType("EventEngine");
        evInstance = ev == null ? null : asm.GetType("PersistenSingleton`1")?.MakeGenericType(ev).GetField("instance", S);
        evFieldmap = ev?.GetField("fieldmap", I);
        fmWalkMesh = asm.GetType("FieldMap")?.GetField("walkMesh", I);
        var cfg = asm.GetType("Memoria.Configuration");
        cfgInstance = cfg?.GetField("Instance", S);
        cfgControl = cfg?.GetField("_control", I);
        ctlPsxMove = cfgControl?.FieldType.GetField("PSXMovementMethod", I);
        iniValue = ctlPsxMove?.FieldType.GetField("Value", I);
        walkFixReady = evInstance != null && evFieldmap != null && fmWalkMesh != null && cfgInstance != null && cfgControl != null && ctlPsxMove != null && iniValue != null;
        if (!walkFixReady) Log.Warn("fields without a walkmesh: this Memoria differs; MovePC is left as it is");
    }

    /// Before the frame's Update: PSXMovementMethod off while the field has no walkmesh.
    public static void FieldTick() {
        if (!walkFixReady || Core.Player.LastSceneLoaded != "FieldMap") { if (psxOff) RestorePsx(); return; }
        object? fm = evFieldmap!.GetValue(evInstance!.GetValue(null));
        // no field map yet (or the last field's, gone) counts too: the field's first frame comes
        // before EventEngine has the new one
        bool noWalk = !(fm is UnityEngine.Object u && u != null) || fmWalkMesh!.GetValue(fm) == null;
        if (noWalk == psxOff) return;
        if (!noWalk) { RestorePsx(); return; }
        psxIni = ctlPsxMove!.GetValue(cfgControl!.GetValue(cfgInstance!.GetValue(null)));
        if (psxIni == null || !(bool)iniValue!.GetValue(psxIni)!) { psxIni = null; return; }
        iniValue.SetValue(psxIni, false);
        psxOff = true;
    }

    static void RestorePsx() {
        if (psxIni != null) iniValue!.SetValue(psxIni, true);
        psxIni = null;
        psxOff = false;
    }

    static void Invoke(MethodInfo m, object self) {
        try { m.Invoke(self, null); }
        catch (TargetInvocationException e) when (e.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
    }

    // Thread.Start(object) on a thread made with a plain ThreadStart: .NET Framework (Unity's
    // Mono) ignored the argument, .NET 8 throws. Memoria's VoicePlayer.StartSound starts its
    // voice watcher that way, so no voice line played and, with AutoDismissDialogAfterCompletion,
    // each dialog closed at once and the next came over it (GitHub issues 22, 23, 26: KupoMail).
    // Thread.Start(object, bool, bool) is replaced by the same steps without that check.
    static FieldInfo? startHelper, startArg, startContext;
    static MethodInfo? startCore, throwIfNoStart;

    static void InstallThreadStart() {
        const BindingFlags I = BindingFlags.Instance | BindingFlags.NonPublic, S = BindingFlags.Static | BindingFlags.NonPublic;
        var t = typeof(System.Threading.Thread);
        var target = t.GetMethod("Start", I, null, new[] { typeof(object), typeof(bool), typeof(bool) }, null);
        startHelper = t.GetField("_startHelper", I);
        var helperType = startHelper?.FieldType;
        startArg = helperType?.GetField("_startArg", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        startContext = helperType?.GetField("_executionContext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        startCore = t.GetMethod("StartCore", I);
        throwIfNoStart = t.GetMethod("ThrowIfNoThreadStart", S);
        if (target == null || startHelper == null || startArg == null || startContext == null || startCore == null) {
            Log.Warn("Thread.Start(object): this runtime's Thread differs; not replaced");
            return;
        }
        Detour(target, M(nameof(ThreadStartWithArg)));
    }

    static void ThreadStartWithArg(System.Threading.Thread self, object? parameter, bool captureContext, bool internalThread) {
        try { throwIfNoStart?.Invoke(null, new object[] { internalThread }); }
        catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
        object? helper = startHelper!.GetValue(self);
        if (helper != null) {
            startArg!.SetValue(helper, parameter);      // used by a ParameterizedThreadStart, ignored by a ThreadStart
            startContext!.SetValue(helper, captureContext ? System.Threading.ExecutionContext.Capture() : null);
        }
        try { startCore!.Invoke(self, null); }
        catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
    }

    /// FF9_TEST_THREADSTART=1: a plain ThreadStart started with an argument, as VoicePlayer does.
    public static void TestThreadStart() {
        if (Environment.GetEnvironmentVariable("FF9_TEST_THREADSTART") == null) return;
        try {
            var done = new System.Threading.ManualResetEventSlim();
            var th = new System.Threading.Thread(() => done.Set());
            th.Start("an argument");
            Log.Info(done.Wait(2000) ? "test thread start: a ThreadStart with an argument ran" : "test thread start: it did not run");
            var got = new object?[1];
            var th2 = new System.Threading.Thread(o => { got[0] = o; });
            th2.Start("passed");
            th2.Join(2000);
            Log.Info($"test thread start: a ParameterizedThreadStart got '{got[0]}'");
        } catch (Exception e) { Log.Warn("test thread start: " + e); }
    }

    /// .NET Framework accepted relative paths (Memoria loads "StreamingAssets/Scripts/Memoria.Scripts.dll").
    static Assembly LoadFile(string path) => AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));

    // Steam Cloud saves. The title screen's Cloud menu (Upload and Download in SiliconStudio.Social)
    // reads and writes one file, FF9_CloudSaveFile_ww.sav (_jp for the Japanese game).
    // On the PC it lives in <My Documents>\Square Enix\FINAL FANTASY IX, where Steam picks it up.
    // That Windows path doesn't work on Android, so the game is pointed at the folder named by
    // FF9_CLOUD_DIR instead. The launcher keeps that folder in step with Steam Cloud (SteamFiles.syncCloud).
    static string? cloudDir;
    static MethodInfo? cloudFileName;

    static void InstallCloudFolder() {
        cloudDir = Environment.GetEnvironmentVariable("FF9_CLOUD_DIR");
        if (string.IsNullOrEmpty(cloudDir)) return;
        const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic;
        var social = Type.GetType("SiliconStudio.Social, Assembly-CSharp");
        cloudFileName = social?.GetMethod("GetCloudSaveFileName", S);
        var root = social?.GetMethod("GetRootDir_SteamCloud", S);
        var file = social?.GetMethod("GetFilePath_SteamCloud", S);
        if (root == null || file == null || cloudFileName == null) { Log.Warn("cloud saves: the game's Cloud path functions were not found"); return; }
        Detour(root, M(nameof(CloudRootDir)));
        Detour(file, M(nameof(CloudFilePath)));
        // Upload (Steam_Save's iterator) is replaced as well. Mono can copy small functions like those two
        // (16 to 21 bytes of IL, depending on the Memoria build) into the code that calls them, which skips
        // the redirect above. Upload then writes to "/data/Documents\Square Enix\...", which isn't writable,
        // and fails without an error. Its MoveNext is called through IEnumerator, so it is never copied.
        const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type? iter = null, response = null;
        foreach (var n in social!.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)) {
            if (n.Name.StartsWith("<Steam_Save>")) iter = n;
            if (n.Name == "ResponseData") response = n;
        }
        var moveNext = iter?.GetMethod("MoveNext", I);
        // The iterator's field names depend on the compiler that built Memoria's Assembly-CSharp:
        // "$PC" and "$current" (Mono's mcs) or "<>1__state" and "<>2__current" (Roslyn). Both work the same way.
        savePC = iter?.GetField("$PC", I) ?? iter?.GetField("<>1__state", I);
        saveCurrent = iter?.GetField("$current", I) ?? iter?.GetField("<>2__current", I);
        saveData = iter?.GetField("data", I);
        responseCtor = response?.GetConstructor(I, null, new[] { typeof(object), typeof(object) }, null);
        responseData = response?.GetField("m_data", I);
        responseStatus = response?.GetField("m_status", I);
        if (moveNext == null || savePC == null || saveCurrent == null || saveData == null || responseCtor == null || responseData == null || responseStatus == null)
            Log.Warn("cloud saves: the game's Upload was not found; it may not reach the cloud folder");
        else Detour(moveNext, M(nameof(CloudSaveMoveNext)));
        Log.Info($"cloud saves: {cloudDir} (My Documents here: '{Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}')");
    }

    static FieldInfo? savePC, saveCurrent, saveData, responseData, responseStatus;
    static ConstructorInfo? responseCtor;

    /// Replaces Social.Steam_Save's iterator and works the same way. It writes the Cloud file, returns one
    /// ResponseData (m_data is true on success, m_status is UnknownError on failure), then ends.
    static bool CloudSaveMoveNext(object self) {
        if ((int)savePC!.GetValue(self)! != 0) { savePC.SetValue(self, -1); return false; }
        object resp = responseCtor!.Invoke(new object?[] { null, null });
        bool ok = false;
        try {
            Directory.CreateDirectory(cloudDir!);
            string path = CloudFilePath();
            File.WriteAllBytes(path, (byte[])saveData!.GetValue(self)!);
            ok = true;
            Log.Info("cloud saves: Upload wrote " + path);
        } catch (Exception e) {
            Log.Warn("cloud saves: Upload failed: " + e.Message);
            responseStatus!.SetValue(resp, Enum.ToObject(responseStatus.FieldType, 1));   // ResponseData.Status.UnknownError
        }
        responseData!.SetValue(resp, ok);
        saveCurrent!.SetValue(self, resp);
        savePC.SetValue(self, 1);
        return true;
    }

    /// Replaces the game's GetRootDir_SteamCloud. Returns the Android folder for the Cloud file.
    static string CloudRootDir() { Log.Info("cloud saves: the Cloud menu's folder is " + cloudDir); return cloudDir!; }

    /// Replaces the game's GetFilePath_SteamCloud. Returns the Cloud file's full path in the Android folder.
    static string CloudFilePath() {
        string path = Path.Combine(cloudDir!, (string)cloudFileName!.Invoke(null, null)!);
        Log.Info("cloud saves: the Cloud menu uses " + path);
        return path;
    }
}
