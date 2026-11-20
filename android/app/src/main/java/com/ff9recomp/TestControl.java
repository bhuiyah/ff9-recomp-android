package com.ff9recomp;

import android.app.ActivityManager;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Bundle;
import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.util.HashMap;
import java.util.Map;

/**
 * A test API over adb (ported from FF7Recomp). Only adb can call it: the receiver requires
 * android.permission.DUMP, which the shell holds and apps cannot get. The answer (JSON) is the
 * broadcast's result data, which `am broadcast` prints.
 *
 *   am broadcast -a com.ff9recomp.test.GET    -n com.ff9recomp/.TestControl
 *   am broadcast -a com.ff9recomp.test.SET    -n com.ff9recomp/.TestControl --ez widescreen true --ei speed 2
 *   am broadcast -a com.ff9recomp.test.STOP   -n com.ff9recomp/.TestControl
 *   am broadcast -a com.ff9recomp.test.REPORT -n com.ff9recomp/.TestControl [--ez saves true] [--ez state true]
 *   am start -n com.ff9recomp/.LauncherActivity -f 0x24000000 --ez test_play true
 *   am broadcast -a com.ff9recomp.test.PAD -p com.ff9recomp --ei buttons 0x1000 --ei ms 100   (GameActivity)
 *   am broadcast -a com.ff9recomp.test.SET -n com.ff9recomp/.TestControl --es storage_custom /sdcard/FF9RecompTest
 *     (use the files in that folder, moving nothing; "" goes back to the app's own folder. Restart the launcher after.)
 *
 * SET takes typed extras (--ei / --ez / --es); unknown names are refused.
 */
public class TestControl extends BroadcastReceiver {
    private static volatile boolean memoriaBusy;
    private static volatile String memoriaStatus = "";

    static final String PREFIX = "com.ff9recomp.test.";
    static final String EXTRA_PLAY = "test_play";

    /** The launcher's settings and their types (Settings.java applies them to Memoria.ini). */
    static final String TEST_SCREEN = "test_screen";
    static final Map<String, Class<?>> TYPES = new HashMap<>();
    static {
        for (String k : Settings.INTS) TYPES.put(k, Integer.class);
        for (String k : Settings.BOOLS) TYPES.put(k, Boolean.class);
        TYPES.put("guide_url", String.class);
        // On-screen controls > Size and layout: right stick, L3 and R3
        TYPES.put("touch_extra", Boolean.class);
        for (String k : GameActivity.HOTKEYS) TYPES.put(k, Integer.class);   // key codes, 0 = none
        TYPES.put("touch_left_stick", Boolean.class);
        TYPES.put("touch_vibrate", Boolean.class);
        // Game files > Storage's own folder ("" = the app's own folder), switched without moving anything (tests)
        TYPES.put("storage_custom", String.class);
    }

    @Override
    public void onReceive(Context ctx, Intent intent) {
        String a = intent.getAction();
        if (a == null || !a.startsWith(PREFIX)) return;
        SharedPreferences prefs = ctx.getSharedPreferences("launcher", Context.MODE_PRIVATE);
        JSONObject out = new JSONObject();
        try {
            switch (a.substring(PREFIX.length())) {
            case "GET":
                break;
            case "SET": {
                Bundle b = intent.getExtras();
                SharedPreferences.Editor e = prefs.edit();
                JSONObject changed = new JSONObject();
                java.io.File ini = new java.io.File(new GameInstall(ctx).game, "Memoria.ini");
                java.util.List<String> iniLines = null;
                for (String k : b == null ? new java.util.HashSet<String>() : b.keySet()) {
                    if (k.startsWith("memoria.")) {     // memoria.Section.Key: written into Memoria.ini, as the Memoria settings page does
                        int dot = k.indexOf('.', 8);
                        if (dot < 0) { out.put("error", "use memoria.Section.Key, not " + k); continue; }
                        if (iniLines == null) iniLines = MemoriaSettings.read(ini);
                        String v = String.valueOf(b.get(k)).trim();
                        MemoriaSettings.apply(iniLines, new MemoriaSettings.Choice("test", k.substring(8) + "=" + v));
                        changed.put(k, v);
                        continue;
                    }
                    Class<?> t = TYPES.get(k);
                    Object v = b.get(k);
                    if (t == null) { out.put("error", "unknown setting " + k); continue; }
                    if (t == Integer.class) { int i = v instanceof Number ? ((Number) v).intValue() : Integer.parseInt(String.valueOf(v).trim()); e.putInt(k, i); changed.put(k, i); }
                    else if (t == String.class) { String str = v == null ? "" : String.valueOf(v); e.putString(k, str); changed.put(k, str); }
                    else { boolean z = v instanceof Boolean ? (Boolean) v : Boolean.parseBoolean(String.valueOf(v).trim()); e.putBoolean(k, z); changed.put(k, z); }
                }
                e.commit();
                if (iniLines != null) MemoriaSettings.write(ini, iniLines);
                out.put("set", changed);
                break;
            }
            case "STOP": {
                int pid = gamePid(ctx);
                if (pid > 0) android.os.Process.killProcess(pid);
                out.put("stopped", pid > 0);
                break;
            }
            case "REPORT": {
                File zip = Reports.build(ctx, intent.getBooleanExtra("saves", false), intent.getBooleanExtra("state", false));
                File to = new File(new GameInstall(ctx).files, "report-test.zip");
                try (java.io.InputStream in = new java.io.FileInputStream(zip); java.io.OutputStream o = new java.io.FileOutputStream(to)) {
                    byte[] buf = new byte[1 << 16];
                    for (int n; (n = in.read(buf)) > 0; ) o.write(buf, 0, n);
                }
                JSONArray names = new JSONArray();
                try (java.util.zip.ZipFile z = new java.util.zip.ZipFile(to)) {
                    for (java.util.Enumeration<? extends java.util.zip.ZipEntry> en = z.entries(); en.hasMoreElements(); ) {
                        java.util.zip.ZipEntry ze = en.nextElement();
                        names.put(ze.getName() + " " + ze.getSize());
                    }
                }
                out.put("report", to.getAbsolutePath());
                out.put("entries", names);
                break;
            }
            case "SCREEN": {
                // the launcher laid out as on another screen, in the Thor's top-left corner:
                // --es size 640x480@240 (empty: back to the Thor's own); takes effect when it opens again
                String size = intent.getStringExtra("size");
                if (size == null || !(size.isEmpty() || size.matches("[0-9]+x[0-9]+@[0-9]+"))) { out.put("error", "size WxH@DPI or empty"); break; }
                prefs.edit().putString(TEST_SCREEN, size).commit();
                out.put("screen", size);
                break;
            }
            case "STEAM_CLOUD": {
                // --es op list|sync.
                // "list" lists FINAL FANTASY IX's Steam Cloud files.
                // "sync" runs the same sync as Game files > Saves.
                String op = intent.getStringExtra("op") != null ? intent.getStringExtra("op") : "list";
                if (!op.equals("list") && !op.equals("sync")) { out.put("error", "op list|sync"); break; }
                new Thread(() -> {
                    try {
                        SteamFiles sf = new SteamFiles(new SteamLink(ctx));
                        String r = op.equals("list") ? sf.cloudListing() : String.valueOf(sf.syncCloud(SteamFiles.cloudDir(ctx)));
                        for (String line : r.split("\n")) android.util.Log.i("FF9SteamFiles", "cloud " + op + ": " + line);
                        android.util.Log.i("FF9SteamFiles", "cloud " + op + " done");
                    } catch (Throwable e) { android.util.Log.e("FF9SteamFiles", "cloud " + op + " failed", e); }
                }, "test-cloud").start();
                out.put("cloud", op);
                break;
            }
            case "STEAM_DOWNLOAD": {
                // tests download into a scratch folder only, never the player's game:
                // --es op download|memoria|pause|continue|status|queue_pause|queue_continue --es dir ABS
                // (logcat tags FF9SteamFiles, FF9Memoria, FF9Jobs). The download runs in JobService.
                String op = intent.getStringExtra("op") != null ? intent.getStringExtra("op") : "status";
                String dir = intent.getStringExtra("dir");
                GameInstall gi = new GameInstall(ctx);
                if (op.equals("download") || op.equals("memoria")) {
                    if (dir == null || !dir.startsWith("/")) { out.put("error", "dir: an absolute scratch folder"); break; }
                    File d = new File(dir).getCanonicalFile();
                    if (d.equals(gi.game.getCanonicalFile()) || d.getPath().startsWith(gi.game.getCanonicalPath() + "/")) {
                        out.put("error", "not the player's game folder"); break;
                    }
                    if (op.equals("download")) out.put("started", JobService.start(ctx, JobService.STEAM, d.getPath()));
                    else if (!memoriaBusy) {
                        memoriaBusy = true;
                        new Thread(() -> {
                            try {
                                MemoriaInstall m = new MemoriaInstall(gi);
                                m.fetch(w -> memoriaStatus = w, new java.util.concurrent.atomic.AtomicBoolean());
                                m.install(d, w -> memoriaStatus = w);
                                memoriaStatus = "memoria installed";
                            } catch (Throwable e) {
                                android.util.Log.e("FF9Memoria", "test install", e);
                                memoriaStatus = "memoria failed: " + e;
                            }
                            memoriaBusy = false;
                        }).start();
                    }
                } else if (op.equals("stop") || op.equals("pause")) JobService.pause();
                else if (op.equals("continue")) out.put("continued", JobService.resume(ctx));
                else if (op.equals("test_memoria")) out.put("started", JobService.start(ctx, JobService.TEST_MEMORIA, null));
                else if (op.equals("discard")) JobService.forget(ctx);
                else if (op.equals("queue_pause")) DownloadQueue.get(ctx).pause();
                else if (op.equals("queue_continue")) DownloadQueue.get(ctx).resume();
                else if (op.equals("queue_add")) {          // --es folder <InstallationPath>
                    ModCatalog.Mod m = ModCatalog.byFolder(ctx, String.valueOf(intent.getStringExtra("folder")));
                    out.put("added", m != null && DownloadQueue.get(ctx).add(m, false));
                } else if (op.equals("queue_cancel")) {
                    DownloadQueue q = DownloadQueue.get(ctx);
                    for (DownloadQueue.Item it : q.snapshot()) q.cancel(it);
                }
                out.put("busy", JobService.busy());
                out.put("running", String.valueOf(JobService.running));
                out.put("pending", String.valueOf(JobService.pending(ctx)));
                out.put("paused", JobService.paused);
                out.put("percent", JobService.percent);
                out.put("status", JobService.status);
                out.put("memoria", memoriaBusy + " " + memoriaStatus);
                DownloadQueue q = DownloadQueue.get(ctx);
                DownloadQueue.Item it = q.running();
                out.put("queue", (q.paused() ? "paused " : "") + q.pendingCount() + " pending" + (it != null ? ", " + it.mod.folder + " " + it.phase + " " + it.percent() + "%" : ""));
                break;
            }
            default:
                out.put("error", "unknown action " + a);
            }
            JSONObject all = new JSONObject();
            for (Map.Entry<String, ?> en : prefs.getAll().entrySet())
                if (TYPES.containsKey(en.getKey())) all.put(en.getKey(), en.getValue());
            out.put("settings", all);
            out.put("game_running", gamePid(ctx) > 0);
        } catch (Exception ex) {
            try { out.put("error", ex.toString()); } catch (Exception ignored) { }
        }
        setResultCode(out.has("error") ? 1 : 0);
        setResultData(out.toString());
    }

    static boolean isSetting(String key) { return TYPES.containsKey(key); }

    /** The game's process (":game"), or 0. */
    static int gamePid(Context ctx) {
        ActivityManager am = (ActivityManager) ctx.getSystemService(Context.ACTIVITY_SERVICE);
        if (am.getRunningAppProcesses() == null) return 0;
        for (ActivityManager.RunningAppProcessInfo p : am.getRunningAppProcesses())
            if (p.processName.endsWith(":game")) return p.pid;
        return 0;
    }
}
