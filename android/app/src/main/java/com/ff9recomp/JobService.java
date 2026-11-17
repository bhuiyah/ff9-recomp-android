package com.ff9recomp;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.ServiceInfo;
import android.net.Uri;
import android.os.IBinder;
import android.os.PowerManager;
import android.util.Log;

import java.io.File;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * The launcher's long jobs as a foreground service, so they carry on with the screen off or the
 * launcher in the background (FF7Recomp's BuildService, its GitHub #53): the Steam download,
 * importing the game from a folder, and moving the files to or from the SD card. One at a time.
 * A notification shows the progress, with Pause; a paused job's notification has Continue.
 *
 * Every job keeps what it has done: the Steam download skips files that are already whole, the
 * import skips files already copied, and the move skips files already moved. The job is kept in
 * the launcher's preferences until it finishes, so a job stopped by Pause, by the system or by an
 * update shows Continue in the launcher.
 */
public class JobService extends Service {
    private static final String TAG = "FF9Jobs", CHANNEL = "jobs";
    private static final int ID = 9;
    static final String STEAM = "steam", IMPORT = "import", TO_SD = "to_sd", TO_INTERNAL = "to_internal";
    /** adb tests only (TestControl): Memoria's download, the Steam download's first step, without Steam. */
    static final String TEST_MEMORIA = "test_memoria";
    static final String MEMORIA = "memoria";        // Install Memoria into an imported copy without it
    static final String ACTION_PAUSE = "com.ff9recomp.job.PAUSE", ACTION_CONTINUE = "com.ff9recomp.job.CONTINUE";
    /** job_ran: the pending job has run before (a continued import skips the files copied whole; a new one copies all). */
    private static final String KIND = "job_kind", ARG = "job_arg", RAN = "job_ran";

    /** The job running (one of the kinds), or null. */
    static volatile String running;
    /** What it is doing; when it has ended, how it ended. */
    static volatile String status = "";
    /** 0..100, or -1 when the job can't tell. */
    static volatile int percent = -1;
    /** The last job stopped for Pause (it is still pending). */
    static volatile boolean paused;
    /** Set by Pause: the job stops at its next check and keeps what it has done. */
    static final AtomicBoolean pause = new AtomicBoolean();
    /** The service's worker thread is running. */
    private static volatile boolean working;

    /** Thrown by a job that stopped for Pause. */
    static final class Paused extends java.io.IOException {
        Paused() { super("paused"); }
    }

    static void checkPause() throws Paused { if (pause.get()) throw new Paused(); }

    static boolean busy() { return running != null; }

    private static SharedPreferences prefs(Context c) { return c.getSharedPreferences("launcher", MODE_PRIVATE); }

    /** The job that was started and has not finished (paused or stopped by the system), when none runs. */
    static String pending(Context c) { return running != null ? null : prefs(c).getString(KIND, null); }

    static String title(String kind) {
        if (kind == null) return "";
        switch (kind) {
            case STEAM: return "Downloading FINAL FANTASY IX from Steam";
            case IMPORT: return "Importing FINAL FANTASY IX";
            case TEST_MEMORIA: return "Downloading Memoria (test)";
            case MEMORIA: return "Installing Memoria";
            case TO_SD: return "Moving your files to the SD card";
            default: return "Moving your files to internal storage";
        }
    }

    /** Start a job (arg: the Steam download's folder, the import's folder URI; null otherwise). */
    static boolean start(Context c, String kind, String arg) {
        if (running != null) return false;
        prefs(c).edit().putString(KIND, kind).putString(ARG, arg).putBoolean(RAN, false).commit();
        begin(kind);
        c.startForegroundService(new Intent(c, JobService.class));
        return true;
    }

    /** Continue the pending job. */
    static boolean resume(Context c) {
        String kind = pending(c);
        if (kind == null) return false;
        begin(kind);
        c.startForegroundService(new Intent(c, JobService.class).setAction(ACTION_CONTINUE));
        return true;
    }

    private static void begin(String kind) {
        running = kind;
        paused = false;
        pause.set(false);
        percent = -1;
        status = "Starting";
    }

    static void pause() { if (running != null) pause.set(true); }

    /** Drop the pending job. A move's partial copy is removed; a download's or import's files stay (a new one skips them). */
    static void forget(Context c) {
        if (running != null) return;
        String kind = prefs(c).getString(KIND, null);
        if (TO_SD.equals(kind) || TO_INTERNAL.equals(kind)) Storage.discardMove(c, TO_SD.equals(kind));
        clear(c);
        paused = false;
    }

    /** The job is over: forget it, and give back the import's hold on the picked folder. */
    private static void clear(Context c) {
        SharedPreferences p = prefs(c);
        if (IMPORT.equals(p.getString(KIND, null)) && p.getString(ARG, null) != null) {
            try { c.getContentResolver().releasePersistableUriPermission(Uri.parse(p.getString(ARG, null)), Intent.FLAG_GRANT_READ_URI_PERMISSION); }
            catch (Exception ignored) { }
        }
        p.edit().remove(KIND).remove(ARG).remove(RAN).commit();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.createNotificationChannel(new NotificationChannel(CHANNEL, "Downloads and imports", NotificationManager.IMPORTANCE_LOW));
        String action = intent != null ? intent.getAction() : null;
        if (ACTION_PAUSE.equals(action)) {               // the notification's Pause (a plain service start)
            pause();
            if (!working) stopSelf(startId);
            return START_NOT_STICKY;
        }
        // started with startForegroundService: startForeground comes first, whatever happens next
        String kind = prefs(this).getString(KIND, null);
        startForeground(ID, note(kind), ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
        if (working) return START_NOT_STICKY;            // one job at a time
        if (kind == null) { running = null; stopForeground(STOP_FOREGROUND_REMOVE); stopSelf(); return START_NOT_STICKY; }
        if (running == null) begin(kind);                // Continue from the notification
        nm.cancel(ID + 1);
        working = true;
        String arg = prefs(this).getString(ARG, null);
        boolean ranBefore = prefs(this).getBoolean(RAN, false);
        prefs(this).edit().putBoolean(RAN, true).commit();
        PowerManager.WakeLock wl = getSystemService(PowerManager.class).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "ff9recomp:job");
        wl.acquire(6 * 60 * 60 * 1000L);
        new Thread(() -> {
            Thread ticker = new Thread(() -> {
                try {
                    while (true) {
                        Thread.sleep(2000);
                        nm.notify(ID, note(kind));
                    }
                } catch (InterruptedException ignored) { }
            });
            ticker.start();
            String msg;
            boolean finished = true;
            try {
                msg = run(kind, arg, ranBefore);
            } catch (Paused e) {
                msg = null;
            } catch (Throwable e) {
                Log.e(TAG, kind, e);
                msg = "Stopped: " + (e.getMessage() != null ? e.getMessage() : e.toString()) + ".";
                finished = !(kind.equals(STEAM) || kind.equals(IMPORT));    // those carry on where they stopped
                if (!finished) msg += " Continue goes on where it stopped.";
            }
            ticker.interrupt();
            if (pause.get() && msg == null) {
                paused = true;
                finished = false;
                msg = "Paused. Continue goes on where it stopped; nothing done so far is lost.";
            }
            if (finished) clear(this);
            Log.i(TAG, kind + ": " + msg);
            status = msg;
            running = null;
            if (wl.isHeld()) wl.release();
            stopForeground(STOP_FOREGROUND_REMOVE);
            Notification.Builder done = new Notification.Builder(this, CHANNEL)
                    .setSmallIcon(R.drawable.ic_launcher_monochrome)
                    .setContentIntent(launcher())
                    .setAutoCancel(true)
                    .setContentTitle(paused ? title(kind) + ": paused" : finished ? title(kind) + ": done" : title(kind) + ": stopped")
                    .setContentText(msg)
                    .setStyle(new Notification.BigTextStyle().bigText(msg));
            if (!finished)
                done.addAction(new Notification.Action.Builder(null, "Continue",
                        PendingIntent.getForegroundService(this, 2, new Intent(this, JobService.class).setAction(ACTION_CONTINUE),
                                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT)).build());
            nm.notify(ID + 1, done.build());
            working = false;
            stopSelf();
        }, "ff9-job").start();
        return START_NOT_STICKY;
    }

    /** Runs the job; returns how it ended. Throws Paused when Pause stopped it. */
    private String run(String kind, String arg, boolean ranBefore) throws Exception {
        GameInstall gi = new GameInstall(this);
        switch (kind) {
            case STEAM: {
                if (!new SteamLink(this).signedIn()) return "Sign in to Steam first (Achievements), then download again.";
                String err = new SteamFiles(new SteamLink(this)).downloadGame(gi, arg != null ? new File(arg) : gi.game, w -> status = w, pause);
                if (err == null) return "FINAL FANTASY IX is downloaded from Steam, with Memoria " + MemoriaInstall.VERSION + ".";
                if (err.equals("stopped")) throw new Paused();
                return "Not downloaded: " + err + ".";
            }
            case TEST_MEMORIA:
                try { new MemoriaInstall(gi).fetch(w -> status = w, pause); }
                catch (InterruptedException e) { throw new Paused(); }
                return "Memoria " + MemoriaInstall.VERSION + " is downloaded.";
            case MEMORIA: {
                MemoriaInstall mi = new MemoriaInstall(gi);
                try { mi.fetch(w -> status = w, pause); }
                catch (InterruptedException e) { throw new Paused(); }
                status = "Installing Memoria " + MemoriaInstall.VERSION;
                mi.install(gi.game, w -> status = w);
                gi.configureMemoria();
                return "Memoria " + MemoriaInstall.VERSION + " is installed. Press Play.";
            }
            case IMPORT:
                gi.importFrom(Uri.parse(arg), w -> status = w, ranBefore);
                return "Your FINAL FANTASY IX is ready" + (gi.hasMoguri() ? ", with Moguri Mod." : ".") + " Press Play (or Start).";
            default: {
                boolean toSd = kind.equals(TO_SD);
                Storage.move(this, toSd, w -> status = w);
                return toSd ? "Your files are on the SD card now." : "Your files are in internal storage now.";
            }
        }
    }

    private PendingIntent launcher() {
        return PendingIntent.getActivity(this, 0, new Intent(this, LauncherActivity.class)
                .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP), PendingIntent.FLAG_IMMUTABLE);
    }

    private Notification note(String kind) {
        int pct = percent;
        return new Notification.Builder(this, CHANNEL)
                .setSmallIcon(R.drawable.ic_launcher_monochrome)
                .setContentTitle(title(kind))
                .setContentText(status)
                .setProgress(100, Math.max(pct, 0), pct < 0)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setContentIntent(launcher())
                .addAction(new Notification.Action.Builder(null, "Pause",
                        PendingIntent.getService(this, 1, new Intent(this, JobService.class).setAction(ACTION_PAUSE),
                                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT)).build())
                .build();
    }

    @Override
    public IBinder onBind(Intent intent) { return null; }
}
