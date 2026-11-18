package com.ff9recomp;

import android.util.Log;

import java.io.BufferedInputStream;
import java.io.DataInputStream;
import java.io.EOFException;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.io.RandomAccessFile;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.zip.GZIPInputStream;

/**
 * Memoria for a copy of the game downloaded from Steam (which is the plain game): its Windows
 * installer (Memoria.Patcher.exe, from Memoria's own GitHub releases, never shipped in the APK)
 * carries its files as a gzip stream after the program, which is unpacked here as the installer
 * does it on the PC (Memoria.Patcher/Program.cs): "{PLATFORM}" is x64, the Windows-only files
 * the folder import also leaves out are skipped, and an existing Memoria.ini keeps the player's
 * settings, with the new release's new settings added.
 *
 * The release is pinned to the one this app's engine was tested with.
 */
final class MemoriaInstall {
    private static final String TAG = "FF9Memoria";
    static final String VERSION = "v2026.11.09";
    private static final String URL_PATCHER = "https://github.com/Albeoris/Memoria/releases/download/" + VERSION + "/Memoria.Patcher.exe";
    private static final long SIZE = 91633889L;
    private static final String SHA256 = "2c8ca404c9ccb517e50bb2a8852aa2dea780755b4399143b3ab9ac61a2704f2b";
    private static final long MAGIC = 0x004149524F4D454DL;            // "MEMORIA\0"

    interface Progress { void update(String what); }

    private final File patcher;

    MemoriaInstall(GameInstall install) {
        patcher = new File(new File(install.files, "steam"), "Memoria.Patcher-" + VERSION + ".exe");
    }

    /** Memoria's installer, downloaded once and checked against its SHA-256. */
    File fetch(Progress p, AtomicBoolean cancel) throws Exception {
        if (patcher.isFile() && patcher.length() == SIZE && SHA256.equals(sha256(patcher))) return patcher;
        patcher.getParentFile().mkdirs();
        File part = new File(patcher.getPath() + ".part");
        long have = part.isFile() ? part.length() : 0;
        if (have > SIZE) { part.delete(); have = 0; }
        if (have < SIZE) {
            HttpURLConnection c = (HttpURLConnection) new URL(URL_PATCHER).openConnection();
            c.setRequestProperty("User-Agent", "FF9Recomp");
            c.setConnectTimeout(20000);
            c.setReadTimeout(60000);
            if (have > 0) c.setRequestProperty("Range", "bytes=" + have + "-");
            int code = c.getResponseCode();
            if (code == 200) have = 0;
            else if (code != 206) throw new IOException("GitHub answered HTTP " + code + " for Memoria " + VERSION);
            try (InputStream in = c.getInputStream(); RandomAccessFile o = new RandomAccessFile(part, "rw")) {
                o.setLength(have);
                o.seek(have);
                byte[] b = new byte[1 << 16];
                long shown = -1;
                for (int n; (n = in.read(b)) > 0; ) {
                    if (cancel.get()) throw new InterruptedException("stopped");
                    o.write(b, 0, n);
                    have += n;
                    long mb = have >> 20;
                    if (mb != shown) { shown = mb; JobService.percent = (int) (have * 100 / SIZE); p.update("Downloading Memoria " + VERSION + ": " + mb + " of " + (SIZE >> 20) + " MB"); }
                }
            }
        }
        if (part.length() != SIZE || !SHA256.equals(sha256(part))) {
            part.delete();
            throw new IOException("the Memoria download did not match its checksum");
        }
        if (patcher.exists()) patcher.delete();
        if (!part.renameTo(patcher)) throw new IOException("cannot write " + patcher);
        return patcher;
    }

    /** One file of the installer: its path under the game folder ('/' separated) and size. */
    interface Entry { void file(String path, long size, InputStream data) throws IOException; }

    /** Goes through the installer's files in order (data must be read fully or not at all). */
    static void read(File exe, Entry e) throws IOException {
        long uncompressed, start;
        try (RandomAccessFile raf = new RandomAccessFile(exe, "r")) {
            long len = raf.length(), at = -1;
            if (le64(raf, len - 0x18) == MAGIC) at = len - 0x18;
            // a signed installer: the signature follows the trailer, as the PC installer looks for it
            for (long off = 0x2920; at < 0 && off > 0x2700; off--) if (le64(raf, len - off) == MAGIC) at = len - off;
            if (at < 0) throw new IOException("this is not a Memoria installer");
            uncompressed = le64(raf, at + 8);
            start = le64(raf, at + 16);
        }
        try (FileInputStream fin = new FileInputStream(exe)) {
            if (fin.skip(start) != start) throw new EOFException();
            DataInputStream in = new DataInputStream(new BufferedInputStream(new GZIPInputStream(new BufferedInputStream(fin, 1 << 16), 1 << 16), 1 << 16));
            Map<Integer, String> names = new HashMap<>();
            byte[] nb = new byte[256];
            long left = uncompressed;
            while (left > 0) {
                long size = Integer.toUnsignedLong(Integer.reverseBytes(in.readInt()));
                in.readLong();                                         // the file's time
                int parts = in.readUnsignedByte();
                StringBuilder path = new StringBuilder();
                for (int i = 0; i < parts; i++) {
                    int id = Short.reverseBytes(in.readShort()) & 0xFFFF;
                    String part;
                    if ((id & 0x8000) != 0) {
                        int n = in.readUnsignedByte();
                        in.readFully(nb, 0, n);
                        part = new String(nb, 0, n, StandardCharsets.UTF_8);
                        names.put(id & 0x7FFF, part);
                    } else {
                        part = names.get(id);
                        if (part == null) throw new IOException("the Memoria installer is damaged");
                    }
                    if (i > 0) path.append('/');
                    path.append(part.equals("{PLATFORM}") ? "x64" : part);
                }
                Bounded data = new Bounded(in, size);
                e.file(path.toString(), size, data);
                data.drain();
                left -= size;
            }
        }
    }

    /** The paths Memoria writes (lower case), so a Steam download leaves those files alone. */
    static Set<String> paths(File exe) throws IOException {
        Set<String> s = new LinkedHashSet<>();
        read(exe, (path, size, data) -> { if (!GameInstall.skipped(path)) s.add(path.toLowerCase(Locale.ROOT)); });
        return s;
    }

    /** Installs Memoria into game, keeping the player's Memoria.ini settings. */
    void install(File game, Progress p) throws IOException {
        File exe = patcher;
        // old field scripts go first, as the PC installer does
        deleteTree(new File(game, "StreamingAssets/Assets/Resources/CommonAsset"));
        File ini = new File(game, "Memoria.ini");
        byte[] oldIni = ini.isFile() ? Files.readAllBytes(ini.toPath()) : null;
        int[] n = {0};
        byte[] buf = new byte[1 << 16];
        read(exe, (path, size, data) -> {
            if (GameInstall.skipped(path)) return;
            File out = new File(game, path);
            out.getParentFile().mkdirs();
            File tmp = new File(out.getPath() + ".memoria");
            try (OutputStream o = new FileOutputStream(tmp)) { for (int k; (k = data.read(buf)) > 0; ) o.write(buf, 0, k); }
            if (out.exists() && !out.delete()) throw new IOException("cannot replace " + out);
            if (!tmp.renameTo(out)) throw new IOException("cannot write " + out);
            if ((++n[0] & 31) == 0) p.update("Installing Memoria " + VERSION + ": " + n[0] + " files");
        });
        if (oldIni != null) {
            String merged = mergeIni(new String(Files.readAllBytes(ini.toPath()), StandardCharsets.ISO_8859_1),
                    new String(oldIni, StandardCharsets.ISO_8859_1));
            Settings.writeIni(ini, merged.getBytes(StandardCharsets.ISO_8859_1));
        } else if (ini.isFile()) {
            Settings.writeIni(ini, newIniDefaults(new String(Files.readAllBytes(ini.toPath()), StandardCharsets.ISO_8859_1))
                    .getBytes(StandardCharsets.ISO_8859_1));
        }
        Log.i(TAG, "installed Memoria " + VERSION + ": " + n[0] + " files");
    }

    /**
     * A Memoria.ini new to this game (no player's settings yet) with this app's own defaults: the
     * battle swirl plays (BattleSwirlFrames 115, the PlayStation's and Steam's; Memoria's 0 skips it,
     * and players took that for a bug, GitHub issue 24). An imported game's ini is left as it was.
     */
    static String newIniDefaults(String ini) {
        return ini.replaceFirst("(?m)^BattleSwirlFrames[ \\t]*=[ \\t]*0(?=[ \\t]*\\r?$)", "BattleSwirlFrames = 115");
    }

    /**
     * The player's ini with the settings and sections the new one adds (MergeIniFiles in
     * Memoria's installer, without its hotfixes for old formulas).
     */
    static String mergeIni(String newIni, String oldIni) {
        List<String> merged = new ArrayList<>();
        for (String l : oldIni.split("\r?\n", -1)) if (!l.startsWith("\t; ")) merged.add(l);
        while (!merged.isEmpty() && merged.get(merged.size() - 1).isEmpty()) merged.remove(merged.size() - 1);
        int first = 0, last = 0;
        for (String line : newIni.split("\r?\n")) {
            String t = line.trim();
            if (t.isEmpty()) continue;
            if (t.startsWith("[")) {
                String sec = t.substring(0, t.indexOf(']') + 1);
                first = -1;
                for (int i = 0; i < merged.size(); i++) if (merged.get(i).trim().startsWith(sec)) { first = i; break; }
                if (first >= 0) {
                    first++;
                    last = first;
                    while (last < merged.size() && !merged.get(last).trim().startsWith("[")) last++;
                    while (last > 0 && merged.get(last - 1).trim().isEmpty()) last--;
                } else {
                    merged.add("");
                    merged.add(sec);
                    first = last = merged.size();
                }
            } else if (t.startsWith(";")) {
                boolean known = false;
                for (String m : merged) if (m.trim().equals(t)) { known = true; break; }
                if (!known) { merged.add(first, line); first++; last++; }
            } else {
                int cut = indexOfAny(t, " \t=");
                String field = cut > 0 ? t.substring(0, cut) : t;
                boolean known = false;
                for (int i = first; i < last; i++) if (merged.get(i).trim().startsWith(field)) { known = true; break; }
                if (!known) { merged.add(last, line); last++; }
            }
        }
        return String.join("\r\n", merged) + "\r\n";
    }

    private static int indexOfAny(String s, String chars) {
        for (int i = 0; i < s.length(); i++) if (chars.indexOf(s.charAt(i)) >= 0) return i;
        return -1;
    }

    private static long le64(RandomAccessFile f, long at) throws IOException {
        if (at < 0) return 0;
        f.seek(at);
        return Long.reverseBytes(f.readLong());
    }

    static String sha256(File f) throws IOException {
        try (InputStream in = new FileInputStream(f)) {
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            byte[] b = new byte[1 << 20];
            for (int n; (n = in.read(b)) > 0; ) md.update(b, 0, n);
            StringBuilder sb = new StringBuilder();
            for (byte x : md.digest()) sb.append(String.format("%02x", x));
            return sb.toString();
        } catch (java.security.NoSuchAlgorithmException e) {
            throw new IOException(e);
        }
    }

    static void deleteTree(File f) {
        File[] kids = f.listFiles();
        if (kids != null) for (File k : kids) deleteTree(k);
        f.delete();
    }

    /** At most n bytes of in. */
    private static final class Bounded extends InputStream {
        private final InputStream in;
        private long left;
        Bounded(InputStream in, long n) { this.in = in; left = n; }
        @Override public int read() throws IOException {
            if (left <= 0) return -1;
            int b = in.read();
            if (b < 0) throw new EOFException();
            left--;
            return b;
        }
        @Override public int read(byte[] b, int off, int len) throws IOException {
            if (left <= 0) return -1;
            int n = in.read(b, off, (int) Math.min(len, left));
            if (n < 0) throw new EOFException("the Memoria installer ended early");
            left -= n;
            return n;
        }
        void drain() throws IOException {
            byte[] b = new byte[1 << 16];
            while (left > 0) read(b, 0, b.length);
        }
    }
}
