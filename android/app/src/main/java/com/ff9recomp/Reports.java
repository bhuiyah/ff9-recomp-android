package com.ff9recomp;

import android.app.ActivityManager;
import android.app.ApplicationExitInfo;
import android.content.ContentValues;
import android.content.Context;
import android.content.SharedPreferences;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.provider.MediaStore;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.io.RandomAccessFile;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.List;
import java.util.Locale;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

/**
 * Report a problem (ported from FF7Recomp): one zip with what it takes to look into a problem.
 *   report.txt   app version, device (model, chip, Android, RAM, screen refresh rates, power
 *                saving), storage, the game's install (Memoria, Moguri, which files are missing),
 *                the mods in load order, the launcher's settings, how the app's processes last
 *                ended (Android's exit records)
 *   logs/        ff9.log (this run and the one before), setup-error.txt, display.txt (the
 *                screen's refresh rate while playing), env.txt, game/Memoria.log,
 *                game/Memoria.ini, game/Settings.ini
 *   java-crash.txt  the last Java crash, written by the app as it crashes
 *   tombstone.pb the last native crash's record from Android (Android 12+)
 *   anr.txt      the last "not responding" record (Android 11+)
 *   saves/       the game's saves, when asked for
 *   savestates/  the newest save state, when asked for
 * Not in it: the game's files, the mods themselves, the Steam sign-in. Nothing leaves the device here: the zip goes to Downloads
 * and the share sheet. It reads only the app's own files and Android's records for this app:
 * no system log, no commands, no device fingerprint (FF7Recomp 0.1.59 ran logcat and some phones
 * began flagging the app).
 */
final class Reports {
    /** Where testers file issues (the repository the updater uses). */
    static final String ISSUES = "https://github.com/" + Updater.REPO + "/issues/new";
    private static final long MAX_LOG = 16L << 20;
    static final String JAVA_CRASH = "java-crash.txt";

    private Reports() {}

    static File savesDir(Context c) { return new File(new GameInstall(c).game, "persistent/Steam/EncryptedSavedData"); }

    static List<File> saves(Context c) {
        List<File> out = new ArrayList<>();
        File[] fs = savesDir(c).listFiles();
        if (fs != null) for (File f : fs) if (f.isFile()) out.add(f);
        out.sort((a, b) -> a.getName().compareTo(b.getName()));
        return out;
    }

    /** The newest save state (SaveStates' slots), or null. */
    static File latestState(Context c) {
        File best = null;
        File[] fs = SaveStates.dir(c).listFiles();
        if (fs != null) for (File f : fs)
            if (f.isFile() && f.getName().endsWith(".dat") && (best == null || f.lastModified() > best.lastModified())) best = f;
        return best;
    }

    /** How the game's process last ended, in a line ("crashed (native), 13 Nov 22:42"), or null. */
    static String lastGameExit(Context c) {
        if (Build.VERSION.SDK_INT < 30) return null;
        for (ApplicationExitInfo e : exits(c))
            if (e.getProcessName().endsWith(":game"))
                return reason(e) + ", " + new SimpleDateFormat("d MMM HH:mm", Locale.US).format(new Date(e.getTimestamp()));
        return null;
    }

    /** Builds the zip in the app's cache and returns it. */
    static File build(Context c, boolean withSaves, boolean withState) throws IOException {
        File dir = new File(c.getCacheDir(), "reports");
        File[] old = dir.listFiles();
        if (old != null) for (File f : old) f.delete();
        dir.mkdirs();
        String stamp = new SimpleDateFormat("yyyyMMdd-HHmm", Locale.US).format(new Date());
        File zip = new File(dir, "FF9Recomp-report-" + stamp + ".zip");
        GameInstall gi = new GameInstall(c);
        try (ZipOutputStream z = new ZipOutputStream(new java.io.FileOutputStream(zip))) {
            put(z, "report.txt", summary(c).getBytes(StandardCharsets.UTF_8));
            for (String p : new String[] { "ff9.log", "ff9.prev.log", "setup-error.txt", "display.txt", "env.txt" }) {
                File f = new File(gi.files, p);
                if (f.isFile()) putFile(z, "logs/" + p, f, MAX_LOG);
            }
            for (String p : new String[] { "Memoria.log", "Memoria.ini", "Settings.ini" }) {
                File f = new File(gi.game, p);
                if (f.isFile()) putFile(z, "logs/game_" + p, f, MAX_LOG);
            }
            File jc = new File(c.getFilesDir(), JAVA_CRASH);
            if (jc.isFile()) putFile(z, JAVA_CRASH, jc, MAX_LOG);
            byte[] tomb = lastTrace(c, ApplicationExitInfo.REASON_CRASH_NATIVE);
            if (tomb != null) put(z, "tombstone.pb", tomb);
            byte[] anr = lastTrace(c, ApplicationExitInfo.REASON_ANR);
            if (anr != null) put(z, "anr.txt", anr);
            if (withSaves) for (File f : saves(c)) putFile(z, "saves/" + f.getName(), f, Long.MAX_VALUE);
            File st = withState ? latestState(c) : null;
            if (st != null) {
                putFile(z, "savestates/" + st.getName(), st, Long.MAX_VALUE);
                File info = new File(st.getParentFile(), st.getName().replace(".dat", ".txt"));
                if (info.isFile()) putFile(z, "savestates/" + info.getName(), info, MAX_LOG);
            }
        }
        return zip;
    }

    /** A copy in Downloads (no permission needed for the app's own files there); its Uri. */
    static Uri toDownloads(Context c, File zip) throws IOException {
        ContentValues v = new ContentValues();
        v.put(MediaStore.Downloads.DISPLAY_NAME, zip.getName());
        v.put(MediaStore.Downloads.MIME_TYPE, "application/zip");
        v.put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS);
        v.put(MediaStore.Downloads.IS_PENDING, 1);
        Uri uri = c.getContentResolver().insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, v);
        if (uri == null) throw new IOException("Downloads is not available");
        try (OutputStream o = c.getContentResolver().openOutputStream(uri); InputStream in = new FileInputStream(zip)) {
            if (o == null) throw new IOException("Downloads is not available");
            copy(in, o, Long.MAX_VALUE);
        }
        v.clear();
        v.put(MediaStore.Downloads.IS_PENDING, 0);
        c.getContentResolver().update(uri, v, null, null);
        return uri;
    }

    /** A new issue with the device lines filled in, or null while there is no public repository. */
    static Uri issueUrl(Context c) {
        if (ISSUES == null) return null;
        String body = "**What happened**\n<!-- What you did, what you expected, what you saw instead. -->\n\n\n"
                + "**Device**\n- Device and chip: " + device() + "\n- Android version: " + Build.VERSION.RELEASE
                + "\n- FF9Recomp version: " + version(c) + "\n\n"
                + "**Report**\n<!-- Drag the FF9Recomp-report zip from your Downloads folder here. -->\n\n";
        return Uri.parse(ISSUES).buildUpon().appendQueryParameter("labels", "bug").appendQueryParameter("body", body).build();
    }

    static String summary(Context c) {
        StringBuilder s = new StringBuilder();
        s.append("FF9Recomp problem report, ").append(new SimpleDateFormat("yyyy-MM-dd HH:mm:ss Z", Locale.US).format(new Date())).append("\n\n");
        s.append("App: ").append(version(c)).append("\n");
        s.append("Device: ").append(device()).append(" (").append(Build.DEVICE).append(")\n");
        s.append("Android: ").append(Build.VERSION.RELEASE).append(" (API ").append(Build.VERSION.SDK_INT).append(")\n");
        ActivityManager am = (ActivityManager) c.getSystemService(Context.ACTIVITY_SERVICE);
        ActivityManager.MemoryInfo mi = new ActivityManager.MemoryInfo();
        am.getMemoryInfo(mi);
        s.append("ABIs: ").append(String.join(", ", Build.SUPPORTED_ABIS)).append("\n");
        s.append("RAM: ").append(mi.totalMem >> 20).append(" MB, ").append(mi.availMem >> 20).append(" MB free; Java heap ")
         .append(am.getMemoryClass()).append("/").append(am.getLargeMemoryClass()).append(" MB\n");
        android.util.DisplayMetrics dm = c.getResources().getDisplayMetrics();
        s.append("Screen: ").append(dm.widthPixels).append("x").append(dm.heightPixels).append(", ").append(dm.densityDpi).append(" dpi");
        android.hardware.display.DisplayManager dmg = (android.hardware.display.DisplayManager) c.getSystemService(Context.DISPLAY_SERVICE);
        android.view.Display disp = dmg.getDisplay(android.view.Display.DEFAULT_DISPLAY);
        if (disp != null) {   // refresh rates: now and what the screen offers
            s.append(", ").append(Math.round(disp.getMode().getRefreshRate())).append(" Hz now (offers");
            java.util.TreeSet<Integer> rates = new java.util.TreeSet<>();
            for (android.view.Display.Mode m : disp.getSupportedModes()) rates.add(Math.round(m.getRefreshRate()));
            for (int r : rates) s.append(' ').append(r);
            s.append(")");
        }
        s.append("\nDisplays: ").append(dmg.getDisplays().length);
        android.os.PowerManager pm = (android.os.PowerManager) c.getSystemService(Context.POWER_SERVICE);
        if (pm != null) s.append("\nPower saving: ").append(pm.isPowerSaveMode() ? "on" : "off")
                        .append(", thermal status ").append(pm.getCurrentThermalStatus());
        s.append("\n");
        GameInstall gi = new GameInstall(c);
        s.append("Files: ").append(gi.files.getAbsolutePath()).append(Storage.place(c) == Storage.CUSTOM ? " (own folder" + (Storage.customProblem(c) != null ? ", unusable: " + Storage.customProblem(c) : "") + ")" : Storage.onSd(c) ? " (SD card)" : "").append(", ")
         .append(gi.files.getUsableSpace() >> 20).append(" MB free\n");
        s.append("Game: ").append(gi.hasGame() ? "imported" : "not imported");
        String missing = gi.hasGame() ? gi.missingMessage() : null;
        if (missing != null) s.append(" (").append(missing).append(")");
        s.append(gi.hasMoguri() ? ", Moguri Mod found" : "").append("\n");
        File[] top = gi.game.listFiles();
        if (top != null) {
            java.util.TreeSet<String> names = new java.util.TreeSet<>();
            for (File f : top) names.add(f.getName() + (f.isDirectory() ? "/" : ""));
            s.append("Game folder: ").append(String.join(", ", names)).append("\n");
        }
        File prime = new File(gi.game, "x64/FF9_Data/Managed/Memoria.Prime.dll");
        if (prime.isFile()) s.append("Memoria.Prime.dll: ").append(prime.length()).append(" bytes, ")
                             .append(new SimpleDateFormat("yyyy-MM-dd", Locale.US).format(new Date(prime.lastModified()))).append("\n");
        try {
            List<String> mods = ModCatalog.folderNames(c);       // with the options' folders
            s.append("Mods on (highest priority first): ").append(mods.isEmpty() ? "none" : String.join(", ", mods)).append("\n");
        } catch (Exception e) { s.append("Mods: ").append(e.getMessage()).append("\n"); }
        File st = latestState(c);
        s.append("Saves: ").append(saves(c).size()).append(" files; newest save state: ")
         .append(st == null ? "none" : st.getName() + " " + new SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.US).format(new Date(st.lastModified()))).append("\n");
        s.append("Steam: ").append(new SteamLink(c).signedIn() ? "signed in" : "not signed in").append("\n\n");

        s.append("Settings:\n");
        SharedPreferences p = c.getSharedPreferences("launcher", Context.MODE_PRIVATE);
        java.util.TreeMap<String, Object> all = new java.util.TreeMap<>(p.getAll());
        for (String k : all.keySet()) if (TestControl.isSetting(k)) s.append("  ").append(k).append(" = ").append(all.get(k)).append("\n");

        s.append("\nHow this app's processes ended (newest first):\n");
        if (Build.VERSION.SDK_INT >= 30) {
            List<ApplicationExitInfo> ex = exits(c);
            if (ex.isEmpty()) s.append("  none recorded\n");
            SimpleDateFormat f = new SimpleDateFormat("MM-dd HH:mm:ss", Locale.US);
            for (ApplicationExitInfo e : ex) {
                s.append("  ").append(f.format(new Date(e.getTimestamp()))).append("  ").append(e.getProcessName())
                 .append(": ").append(reason(e)).append(", status ").append(e.getStatus());
                if (e.getDescription() != null) s.append(" (").append(e.getDescription()).append(")");
                s.append(", ").append(e.getPss() >> 10).append(" MB\n");
            }
        }
        return s.toString();
    }

    static String version(Context c) {
        try {
            android.content.pm.PackageInfo pi = c.getPackageManager().getPackageInfo(c.getPackageName(), 0);
            return pi.versionName + " (" + pi.getLongVersionCode() + ")";
        } catch (Exception e) {
            return "?";
        }
    }

    static String device() {
        String d = Build.MANUFACTURER + " " + Build.MODEL;
        if (Build.VERSION.SDK_INT >= 31) d += ", " + Build.SOC_MANUFACTURER + " " + Build.SOC_MODEL;
        else d += ", " + Build.HARDWARE;
        return d;
    }

    private static List<ApplicationExitInfo> exits(Context c) {
        if (Build.VERSION.SDK_INT < 30) return new ArrayList<>();
        ActivityManager am = (ActivityManager) c.getSystemService(Context.ACTIVITY_SERVICE);
        try {
            return am.getHistoricalProcessExitReasons(c.getPackageName(), 0, 12);
        } catch (Exception e) {
            return new ArrayList<>();
        }
    }

    private static String reason(ApplicationExitInfo e) {
        switch (e.getReason()) {
            case ApplicationExitInfo.REASON_CRASH_NATIVE: return "crashed (native)";
            case ApplicationExitInfo.REASON_CRASH: return "crashed (Java)";
            case ApplicationExitInfo.REASON_ANR: return "stopped responding";
            case ApplicationExitInfo.REASON_LOW_MEMORY: return "closed for low memory";
            case ApplicationExitInfo.REASON_EXIT_SELF: return "closed itself";
            case ApplicationExitInfo.REASON_SIGNALED: return e.getStatus() == 9 ? "stopped from outside (signal 9)" : "stopped by signal " + e.getStatus();
            case ApplicationExitInfo.REASON_USER_REQUESTED: return "closed by the user";
            case ApplicationExitInfo.REASON_USER_STOPPED: return "force-stopped";
            case ApplicationExitInfo.REASON_EXCESSIVE_RESOURCE_USAGE: return "closed for using too much";
            case ApplicationExitInfo.REASON_INITIALIZATION_FAILURE: return "failed to start";
            case ApplicationExitInfo.REASON_DEPENDENCY_DIED: return "closed with another process";
            default: return "ended (reason " + e.getReason() + ")";
        }
    }

    private static byte[] lastTrace(Context c, int reason) {
        if (Build.VERSION.SDK_INT < (reason == ApplicationExitInfo.REASON_ANR ? 30 : 31)) return null;
        for (ApplicationExitInfo e : exits(c)) {
            if (e.getReason() != reason) continue;
            try (InputStream in = e.getTraceInputStream()) {
                if (in == null) return null;
                ByteArrayOutputStream b = new ByteArrayOutputStream();
                copy(in, b, 8L << 20);
                return b.toByteArray();
            } catch (IOException ex) {
                return null;
            }
        }
        return null;
    }

    /** Java crashes go to files/java-crash.txt (for the report), then on as before. */
    static void installCrashHandler(Context c) {
        File out = new File(c.getApplicationContext().getFilesDir(), JAVA_CRASH);
        Thread.UncaughtExceptionHandler next = Thread.getDefaultUncaughtExceptionHandler();
        if (next instanceof CrashKeeper) return;
        Thread.setDefaultUncaughtExceptionHandler(new CrashKeeper(out, next));
    }

    private static final class CrashKeeper implements Thread.UncaughtExceptionHandler {
        private final File out;
        private final Thread.UncaughtExceptionHandler next;
        CrashKeeper(File out, Thread.UncaughtExceptionHandler next) { this.out = out; this.next = next; }
        @Override public void uncaughtException(Thread t, Throwable e) {
            try (java.io.PrintWriter w = new java.io.PrintWriter(new java.io.FileOutputStream(out))) {
                w.println(new SimpleDateFormat("yyyy-MM-dd HH:mm:ss Z", Locale.US).format(new Date())
                        + "  " + android.app.Application.getProcessName() + ", thread " + t.getName());
                e.printStackTrace(w);
            } catch (Throwable ignored) {
            }
            if (next != null) next.uncaughtException(t, e);
        }
    }

    static void writeText(File f, String text) {
        try (OutputStream o = new java.io.FileOutputStream(f)) {
            o.write(text.getBytes(StandardCharsets.UTF_8));
        } catch (IOException ignored) {
        }
    }

    private static void put(ZipOutputStream z, String name, byte[] data) throws IOException {
        z.putNextEntry(new ZipEntry(name));
        z.write(data);
        z.closeEntry();
    }

    private static void putFile(ZipOutputStream z, String name, File f, long max) throws IOException {
        ZipEntry e = new ZipEntry(name);
        e.setTime(f.lastModified());
        z.putNextEntry(e);
        try (RandomAccessFile r = new RandomAccessFile(f, "r")) {
            long len = r.length();
            if (len > max) {
                r.seek(len - max);
                z.write(("[... the first " + (len - max) + " bytes left out ...]\n").getBytes(StandardCharsets.UTF_8));
            }
            byte[] buf = new byte[1 << 16];
            for (int n; (n = r.read(buf)) > 0; ) z.write(buf, 0, n);
        }
        z.closeEntry();
    }

    private static void copy(InputStream in, OutputStream o, long max) throws IOException {
        byte[] buf = new byte[1 << 16];
        long total = 0;
        for (int n; total < max && (n = in.read(buf)) > 0; total += n) o.write(buf, 0, n);
    }
}
