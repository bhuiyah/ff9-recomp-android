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
        InstallCloudFolder();
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
