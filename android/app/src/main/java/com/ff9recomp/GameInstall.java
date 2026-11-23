package com.ff9recomp;

import android.content.ContentResolver;
import android.content.Context;
import android.database.Cursor;
import android.net.Uri;
import android.provider.DocumentsContract;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;

/**
 * Files on the device: the game in app storage (files/game), imported once from the folder the
 * player picks; the runtime (.NET class libraries, FF9Engine.dll) extracted from the APK into
 * files/runtime.
 */
final class GameInstall {
    interface Progress { void update(String what); }

    final Context ctx;
    final File files, game, runtime;

    GameInstall(Context ctx) {
        this.ctx = ctx;
        File ext = Storage.root(ctx);
        files = ext != null ? ext : ctx.getFilesDir();
        game = new File(files, "game");
        File internal = Storage.internalRoot(ctx);
        runtime = new File(internal != null ? internal : ctx.getFilesDir(), "runtime");   // never on an SD card
    }

    boolean hasGame() {
        return new File(game, "x64/FF9_Data/mainData").isFile();
    }

    boolean hasMoguri() { return moguriFolder() != null; }

    /** The Moguri Mod folder (Memoria's mod manager installs it as "MoguriMain"). */
    String moguriFolder() {
        if (new File(game, "MoguriMain").isDirectory()) return "MoguriMain";
        File[] l = game.listFiles();
        if (l != null) for (File f : l) if (f.isDirectory() && f.getName().toLowerCase().startsWith("moguri")) return f.getName();
        return null;
    }

    static final String[] REQUIRED = {
        "x64/FF9_Data/mainData", "x64/FF9_Data/resources.assets", "x64/FF9_Data/Managed/Assembly-CSharp.dll",
        "x64/FF9_Data/Managed/Memoria.Prime.dll", "StreamingAssets/p0data11.bin", "StreamingAssets/p0data5.bin", "Memoria.ini",
    };

    /**
     * The game's asset bundles, each tens to hundreds of MB. A copy without one (or with a cut one)
     * started to a black screen with no sound (GitHub #56: p0data3/4/62/63/7 missing, p0data61 cut short),
     * since the game skips a bundle it can't find. The p0data101..621 copies of other languages are optional.
     */
    static final String[] BUNDLES = {
        "p0data11", "p0data12", "p0data13", "p0data14", "p0data15", "p0data16", "p0data17", "p0data18", "p0data19",
        "p0data2", "p0data3", "p0data4", "p0data5", "p0data61", "p0data62", "p0data63", "p0data7",
    };
    static final long MIN_BUNDLE = 1 << 20;     // the smallest real one (p0data7) is about 68 MB

    /** The required files missing from (or empty or cut in) the game folder, '/' separated. */
    static List<String> missingFiles(File game) {
        List<String> m = new ArrayList<>();
        for (String r : REQUIRED) { File f = new File(game, r); if (!f.isFile() || f.length() == 0) m.add(r); }
        for (String b : BUNDLES) {
            String r = "StreamingAssets/" + b + ".bin";
            File f = new File(game, r);
            if ((!f.isFile() || f.length() < MIN_BUNDLE || f.length() < declaredSize(f)) && !m.contains(r)) m.add(r);
        }
        return m;
    }

    /**
     * The file size a UnityRaw bundle's header declares (signature, format, two version strings, then
     * the total size as a big-endian u32), or 0 if it can't be read. A copy cut short keeps its header,
     * so this catches a bundle that is big but incomplete (GitHub #56: a cut p0data61 = no music).
     */
    static long declaredSize(File f) {
        byte[] h = new byte[128];
        int n = 0;
        try (InputStream in = new java.io.FileInputStream(f)) {
            for (int k; n < h.length && (k = in.read(h, n, h.length - n)) > 0; ) n += k;
        } catch (IOException e) { return 0; }
        int p = 0;
        for (int str = 0; str < 3; str++) {          // signature, then (after the format) two version strings
            while (p < n && h[p] != 0) p++;
            p++;
            if (str == 0) {
                if (!new String(h, 0, Math.min(p - 1, n), StandardCharsets.US_ASCII).equals("UnityRaw")) return 0;
                p += 4;
            }
        }
        if (p + 4 > n) return 0;
        return ((h[p] & 0xFFL) << 24) | ((h[p + 1] & 0xFFL) << 16) | ((h[p + 2] & 0xFFL) << 8) | (h[p + 3] & 0xFFL);
    }

    String missingMessage() {
        List<String> m = missingFiles(game);
        if (m.contains("x64/FF9_Data/Managed/Memoria.Prime.dll") || m.isEmpty() && !memoriaPatched())
            return "Memoria isn't installed in this copy of the game" + (m.isEmpty() ? " (its game code is the plain game's)" : "")
                    + ". Install Memoria below, or install it on the PC and import the folder again.";
        if (m.isEmpty()) return null;
        return "Files missing or incomplete in the imported copy: " + String.join(", ", m)
                + ". Without them the game starts to a black screen. Import the full game folder again "
                + "(check on the PC that StreamingAssets holds all its p0data files), or use Repair from Steam below.";
    }

    /** Whether Memoria is missing (so Install Memoria can fix the copy). */
    boolean needsMemoria() {
        File prime = new File(game, "x64/FF9_Data/Managed/Memoria.Prime.dll");
        return hasGame() && (!prime.isFile() || !memoriaPatched());
    }

    private static String patchedKey;
    private static boolean patchedValue;

    /**
     * Memoria's game code: its Assembly-CSharp.dll has the SoLoud sound system, the plain game's
     * doesn't. A PC copy can keep Memoria.Prime.dll with the plain game's Assembly-CSharp.dll (a
     * Steam file check puts the original back): the game then started with the PC's sound system,
     * which failed, and its title screen never took input (GitHub issue 20).
     */
    boolean memoriaPatched() {
        File a = new File(game, "x64/FF9_Data/Managed/Assembly-CSharp.dll");
        if (!a.isFile()) return false;
        String key = a.getPath() + ":" + a.length() + ":" + a.lastModified();
        synchronized (GameInstall.class) {
            if (key.equals(patchedKey)) return patchedValue;
        }
        boolean found;
        try {
            // ASCII is one byte a character in ISO-8859-1: a plain text search over the file
            found = new String(Files.readAllBytes(a.toPath()), StandardCharsets.ISO_8859_1).contains("SdLibAPIWithSoloud");
        } catch (IOException | OutOfMemoryError e) {
            return true;        // unreadable here: let the game say
        }
        synchronized (GameInstall.class) { patchedKey = key; patchedValue = found; }
        return found;
    }

    // ---------------------------------------------------------------- import from a folder
    /**
     * Copies the game from the picked folder. resume: a continued import, which skips files already
     * copied whole (same size); a new one copies everything, so importing again repairs a damaged file.
     */
    void importFrom(Uri tree, Progress p, boolean resume) throws IOException {
        ContentResolver cr = ctx.getContentResolver();
        // the game may be anywhere below the picked folder (Steam's common/, a copy in Downloads,
        // a folder named differently): find the one that holds StreamingAssets and x64
        p.update("Looking for FINAL FANTASY IX in the picked folder...");
        String rootId = findGameRoot(cr, tree, DocumentsContract.getTreeDocumentId(tree), 0);
        if (rootId == null)
            throw new IOException("this folder doesn't hold FINAL FANTASY IX (no StreamingAssets and x64 folders found in it or below it). "
                    + "Pick the game's folder, the one with FF9_Launcher.exe in it.");
        game.mkdirs();
        long[] count = {0, 0};
        copyTree(cr, tree, rootId, game, "", p, count, resume);
        configureMemoria();
        p.update("Imported " + count[0] + " files (" + (count[1] >> 20) + " MB).");
    }

    /** The document id of the folder (this one or up to 3 levels below) holding StreamingAssets and x64, or null. */
    private String findGameRoot(ContentResolver cr, Uri tree, String docId, int depth) {
        List<String[]> dirs = new ArrayList<>();
        boolean sa = false, x64 = false;
        try (Cursor c = cr.query(DocumentsContract.buildChildDocumentsUriUsingTree(tree, docId), new String[] {
                DocumentsContract.Document.COLUMN_DOCUMENT_ID, DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                DocumentsContract.Document.COLUMN_MIME_TYPE }, null, null, null)) {
            if (c == null) return null;
            while (c.moveToNext()) {
                if (!DocumentsContract.Document.MIME_TYPE_DIR.equals(c.getString(2))) continue;
                String name = c.getString(1);
                if (name.equalsIgnoreCase("StreamingAssets")) sa = true;
                else if (name.equalsIgnoreCase("x64")) x64 = true;
                else dirs.add(new String[] { c.getString(0), name });
            }
        } catch (Exception e) {
            return null;
        }
        if (sa && x64) return docId;
        if (depth >= 3) return null;
        // likely names first (the Steam folder's own name), then the rest
        dirs.sort((a, b) -> Boolean.compare(!a[1].toLowerCase().contains("fantasy"), !b[1].toLowerCase().contains("fantasy")));
        for (String[] d : dirs) {
            String r = findGameRoot(cr, tree, d[0], depth + 1);
            if (r != null) return r;
        }
        return null;
    }

    private void copyTree(ContentResolver cr, Uri tree, String docId, File dst, String rel, Progress p, long[] count, boolean resume) throws IOException {
        Uri children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, docId);
        try (Cursor c = cr.query(children, new String[] {
                DocumentsContract.Document.COLUMN_DOCUMENT_ID, DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                DocumentsContract.Document.COLUMN_MIME_TYPE, DocumentsContract.Document.COLUMN_SIZE }, null, null, null)) {
            if (c == null) return;
            while (c.moveToNext()) {
                String id = c.getString(0), name = c.getString(1), mime = c.getString(2);
                long size = c.getLong(3);
                String r = rel.isEmpty() ? name : rel + "/" + name;
                if (skipped(r)) continue;
                File out = new File(dst, name);
                if (DocumentsContract.Document.MIME_TYPE_DIR.equals(mime)) {
                    out.mkdirs();
                    copyTree(cr, tree, id, out, r, p, count, resume);
                } else {
                    JobService.checkPause();
                    if (resume && out.isFile() && out.length() == size) continue;    // copied before a pause or a stop
                    p.update("Copying " + r);
                    File part = new File(out.getPath() + ".part");      // whole files only: a cut copy is redone
                    try (InputStream in = cr.openInputStream(DocumentsContract.buildDocumentUriUsingTree(tree, id));
                         OutputStream o = new FileOutputStream(part)) {
                        byte[] buf = new byte[1 << 20];
                        int n;
                        while ((n = in.read(buf)) > 0) { JobService.checkPause(); o.write(buf, 0, n); }
                    }
                    if (out.exists()) out.delete();
                    if (!part.renameTo(out)) throw new IOException("cannot write " + out);
                    count[0]++;
                    count[1] += size;
                }
            }
        }
    }

    /**
     * Windows-only parts of the install, not needed on Android (rel: a path under the game folder,
     * '/' separated): the x86 build, programs, the Mono runtime and the plugins, except the battle
     * effects plugin, which the app runs in its x86-64 interpreter.
     */
    static boolean skipped(String rel) {
        String l = rel.replace('\\', '/').toLowerCase(java.util.Locale.ROOT);
        if (l.equals("x86") || l.startsWith("x86/") || l.endsWith(".exe")) return true;
        if (l.equals("x64/ff9_data/mono") || l.startsWith("x64/ff9_data/mono/")) return true;
        return l.startsWith("x64/ff9_data/plugins/") && !l.equals("x64/ff9_data/plugins/ff9specialeffectplugin.dll");
    }

    /** Memoria settings the Android build needs: the SoLoud audio backend. */
    void configureMemoria() throws IOException {
        File ini = new File(game, "Memoria.ini");
        if (!ini.isFile()) return;
        // byte for byte: the file is not valid UTF-8, and a UTF-8 round trip would damage it
        String s = new String(Files.readAllBytes(ini.toPath()), StandardCharsets.ISO_8859_1);
        s = s.replaceAll("(?m)^Backend\\s*=\\s*\\d+", "Backend = 2");
        // a Moguri Mod folder copied in but not enabled: enable it as Memoria's mod manager does
        String moguri = moguriFolder();
        if (moguri != null && java.util.regex.Pattern.compile("(?m)^FolderNames\\s*=\\s*$").matcher(s).find()) {
            s = s.replaceFirst("(?m)^FolderNames\\s*=\\s*$", "FolderNames = \"" + moguri + "\"");
            s = s.replaceFirst("(?m)^TileSize\\s*=\\s*32\\s*$", "TileSize = 64");
        }
        Files.write(ini.toPath(), s.getBytes(StandardCharsets.ISO_8859_1));
    }

    // ---------------------------------------------------------------- runtime from the APK
    void extractRuntime() throws IOException {
        File stamp = new File(runtime, ".version");
        // the install's update time (changes with every install or update; the APK file's own
        // timestamp did not, so a new engine was not unpacked)
        String ver;
        try {
            android.content.pm.PackageInfo pi = ctx.getPackageManager().getPackageInfo(ctx.getPackageName(), 0);
            ver = pi.getLongVersionCode() + "-" + pi.lastUpdateTime;
        } catch (android.content.pm.PackageManager.NameNotFoundException e) {
            ver = "0";
        }
        if (stamp.isFile() && new String(Files.readAllBytes(stamp.toPath()), StandardCharsets.UTF_8).equals(ver)) return;
        runtime.mkdirs();
        try (ZipInputStream z = new ZipInputStream(ctx.getAssets().open("runtime.zip"))) {
            ZipEntry e;
            byte[] buf = new byte[1 << 16];
            while ((e = z.getNextEntry()) != null) {
                File out = new File(runtime, e.getName());
                if (e.isDirectory()) { out.mkdirs(); continue; }
                out.getParentFile().mkdirs();
                // a file the app may not write (copied in by another app or over USB): replaced
                if (out.exists() && !out.canWrite()) out.delete();
                try (OutputStream o = new FileOutputStream(out)) { int n; while ((n = z.read(buf)) > 0) o.write(buf, 0, n); }
            }
        }
        Files.write(stamp.toPath(), ver.getBytes(StandardCharsets.UTF_8));
    }
}
