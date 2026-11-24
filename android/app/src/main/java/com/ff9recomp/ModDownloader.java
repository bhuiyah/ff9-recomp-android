package com.ff9recomp;

import android.content.Context;

import java.io.BufferedInputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.io.RandomAccessFile;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.Locale;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;

/**
 * Installs a catalog mod: downloads its archive (resumable: a cut download continues where it
 * stopped), unpacks it (.zip in Java, .7z in native code: src/launcher/un7z.c, from FF7Recomp),
 * puts its folder at game/<InstallationPath> (replacing an older version) and turns it on in
 * Memoria.ini. One install at a time; the launcher shows the progress.
 */
final class ModDownloader {
    static { System.loadLibrary("ff9launcher"); }
    private static native String nativeUn7z(String archive, String outDir);
    private static native void nativeUn7zCancel();
    private static native long[] nativeUn7zProgress();

    static volatile boolean busy, cancelled;
    static volatile String status = "";

    /** Progress for the download queue: the phase ("Downloading", "Unpacking"), bytes so far and in all. */
    interface Progress { void update(String phase, long got, long total); }
    private static final Progress NONE = (a, b, t) -> { };

    private static final String UNPACK = ".unpack-";

    private ModDownloader() {}

    /** Unpack folders of installs that were cut short (the app was closed while unpacking): gigabytes nothing uses. */
    static File[] leftovers(Context c) {
        File[] f = new GameInstall(c).game.listFiles((d, n) -> n.startsWith(UNPACK));
        return f != null ? f : new File[0];
    }

    static void cancel() {
        cancelled = true;
        try { nativeUn7zCancel(); } catch (Throwable ignored) { }
    }

    /** Runs on the caller's thread. */
    static void install(Context c, ModCatalog.Mod m) throws IOException { install(c, m, NONE); }

    static void install(Context c, ModCatalog.Mod m, Progress pr) throws IOException {
        busy = true;
        cancelled = false;
        try {
            File dl = new File(ModCatalog.dir(c), "downloads");
            dl.mkdirs();
            for (File f : leftovers(c)) Storage.deleteTree(f);
            File archive = archive(c, m);
            download(c, m, archive, pr);
            File game = new GameInstall(c).game;
            File unpack = new File(game, UNPACK + System.nanoTime());
            try {
                status = "Unpacking " + m.name;
                pr.update("Unpacking", 0, 0);
                // by what the file is, not its link: KupoMail's link (Google Drive) has no ".7z" in
                // it and the catalog gives no format, so its 7z was read as a zip, nothing came
                // out, and the move failed (GitHub issue 17)
                String kind = kind(archive);
                if (kind.equals("7z")) {
                    Thread watch = new Thread(() -> {
                        while (busy && status.startsWith("Unpacking")) {
                            try {
                                long[] p = nativeUn7zProgress();
                                if (p != null && p[1] > 0) { status = "Unpacking " + m.name + ": " + (100 * p[0] / p[1]) + "%"; pr.update("Unpacking", p[0], p[1]); }
                                Thread.sleep(500);
                            } catch (Throwable e) { return; }
                        }
                    });
                    watch.setDaemon(true);
                    watch.start();
                    String err = nativeUn7z(archive.getPath(), unpack.getPath());
                    if (err != null) throw new IOException(err);
                } else if (kind.equals("zip")) {
                    unzip(archive, unpack);
                } else {
                    throw new IOException("the download is not a zip or 7z archive (" + kind + ")");
                }
                if (cancelled) throw new IOException("cancelled");
                String[] got = unpack.list();
                if (got == null || got.length == 0) throw new IOException("nothing came out of the archive");
                File root = modRoot(unpack, m.folder);
                File dest = ModCatalog.folder(c, m);
                if (dest.exists()) Storage.deleteTree(dest);
                if (!root.renameTo(dest)) throw new IOException("cannot move the mod into the game folder");
            } finally {
                if (unpack.exists()) Storage.deleteTree(unpack);
            }
            ModCatalog.setInstalledVersion(c, m);
            ModCatalog.setEnabled(c, m, true);
            archive.delete();
            status = m.name + " " + m.version + " installed and turned on.";
        } finally {
            busy = false;
        }
    }

    /** Where a mod's download is kept while it comes in (a cut download resumes from it). */
    static File archive(Context c, ModCatalog.Mod m) {
        String ext = m.url.toLowerCase(Locale.ROOT).contains(".7z") || m.format.equalsIgnoreCase("7z") ? ".7z" : ".zip";
        return new File(new File(ModCatalog.dir(c), "downloads"), m.folder.replaceAll("[^A-Za-z0-9._-]", "_") + "-" + m.version + ext);
    }

    /** "7z" or "zip" by the file's first bytes, else what they look like ("rar", "html", "unknown"). */
    static String kind(File f) throws IOException {
        byte[] h = new byte[8];
        int n;
        try (InputStream in = new FileInputStream(f)) { n = in.read(h); }
        if (n >= 6 && h[0] == '7' && h[1] == 'z' && (h[2] & 0xFF) == 0xBC && (h[3] & 0xFF) == 0xAF && h[4] == 0x27 && h[5] == 0x1C) return "7z";
        if (n >= 4 && h[0] == 'P' && h[1] == 'K' && (h[2] == 3 || h[2] == 5) && (h[3] == 4 || h[3] == 6)) return "zip";
        if (n >= 4 && h[0] == 'R' && h[1] == 'a' && h[2] == 'r' && h[3] == '!') return "rar";
        if (n >= 1 && h[0] == '<') return "a web page, not the file";
        return "unknown";
    }

    /** The folder holding the mod in the unpacked archive: the one named like it, else a lone top folder, else the top. */
    static File modRoot(File unpacked, String folder) {
        File[] top = unpacked.listFiles();
        if (top == null) return unpacked;
        for (File f : top) if (f.isDirectory() && f.getName().equalsIgnoreCase(folder)) return f;
        if (top.length == 1 && top[0].isDirectory() && !new File(unpacked, "ModDescription.xml").isFile()) return top[0];
        return unpacked;
    }

    private static void download(Context c, ModCatalog.Mod m, File to, Progress pr) throws IOException {
        long have = to.isFile() ? to.length() : 0;
        HttpURLConnection h = (HttpURLConnection) new URL(m.url).openConnection();
        h.setRequestProperty("User-Agent", "FF9Recomp/" + Updater.installedVersion(c));
        h.setInstanceFollowRedirects(true);
        h.setConnectTimeout(20000);
        h.setReadTimeout(60000);
        if (have > 0) h.setRequestProperty("Range", "bytes=" + have + "-");
        int code = h.getResponseCode();
        if (code == 416) return;                                 // already complete
        if (code != 200 && code != 206) throw new IOException("the download answered " + code);
        if (code == 200) have = 0;                               // the server does not resume: start over
        long total = h.getContentLengthLong() > 0 ? h.getContentLengthLong() + have : -1;
        File where = new GameInstall(c).files;
        if (total > 0 && where.getUsableSpace() < (total - have) + total * 2)
            throw new IOException("not enough free space: the mod needs about " + Storage.human(total * 3) + ", "
                    + Storage.human(where.getUsableSpace()) + " free");
        try (InputStream in = new BufferedInputStream(h.getInputStream(), 1 << 16);
             RandomAccessFile o = new RandomAccessFile(to, "rw")) {
            o.setLength(have);
            o.seek(have);
            byte[] b = new byte[1 << 16];
            long got = have, shown = 0;
            for (int n; (n = in.read(b)) > 0; ) {
                if (cancelled) throw new IOException("cancelled");
                o.write(b, 0, n);
                got += n;
                if (got - shown >= (1L << 20)) {
                    shown = got;
                    pr.update("Downloading", got, total);
                    status = "Downloading " + m.name + ": " + Storage.human(got) + (total > 0 ? " of " + Storage.human(total) : "");
                }
            }
        }
    }

    private static void unzip(File zip, File out) throws IOException {
        String base = out.getCanonicalPath() + File.separator;
        try (ZipInputStream z = new ZipInputStream(new BufferedInputStream(new FileInputStream(zip), 1 << 16))) {
            byte[] b = new byte[1 << 16];
            for (ZipEntry e; (e = z.getNextEntry()) != null; ) {
                if (cancelled) throw new IOException("cancelled");
                File f = new File(out, e.getName());
                if (!f.getCanonicalPath().startsWith(base)) throw new IOException("the archive has a bad path: " + e.getName());
                if (e.isDirectory()) { f.mkdirs(); continue; }
                f.getParentFile().mkdirs();
                try (OutputStream o = new FileOutputStream(f)) {
                    for (int n; (n = z.read(b)) > 0; ) o.write(b, 0, n);
                }
            }
        }
    }

    static void remove(Context c, ModCatalog.Mod m) throws IOException {
        ModCatalog.setEnabled(c, m, false);
        Storage.deleteTree(ModCatalog.folder(c, m));
        new File(ModCatalog.dir(c), m.folder.replaceAll("[^A-Za-z0-9._-]", "_") + ".version").delete();
    }
}
