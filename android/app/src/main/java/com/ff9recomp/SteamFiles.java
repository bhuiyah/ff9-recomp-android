package com.ff9recomp;

import android.util.Log;

import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.RandomAccessFile;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;

import in.dragonbra.javasteam.enums.EDepotFileFlag;
import in.dragonbra.javasteam.enums.EResult;
import in.dragonbra.javasteam.steam.cdn.Server;
import in.dragonbra.javasteam.steam.handlers.steamapps.PICSProductInfo;
import in.dragonbra.javasteam.steam.handlers.steamapps.PICSRequest;
import in.dragonbra.javasteam.steam.handlers.steamapps.SteamApps;
import in.dragonbra.javasteam.steam.handlers.steamapps.callback.DepotKeyCallback;
import in.dragonbra.javasteam.steam.handlers.steamapps.callback.PICSProductInfoCallback;
import in.dragonbra.javasteam.steam.handlers.steamapps.callback.PICSTokensCallback;
import in.dragonbra.javasteam.steam.handlers.steamcloud.AppFileChangeList;
import in.dragonbra.javasteam.steam.handlers.steamcloud.AppFileInfo;
import in.dragonbra.javasteam.steam.handlers.steamcloud.FileDownloadInfo;
import in.dragonbra.javasteam.steam.handlers.steamcloud.FileUploadBlockDetails;
import in.dragonbra.javasteam.steam.handlers.steamcloud.FileUploadInfo;
import in.dragonbra.javasteam.steam.handlers.steamcloud.HttpHeaders;
import in.dragonbra.javasteam.steam.handlers.steamcloud.SteamCloud;
import in.dragonbra.javasteam.steam.handlers.steamcontent.SteamContent;
import in.dragonbra.javasteam.types.ChunkData;
import in.dragonbra.javasteam.types.DepotManifest;
import in.dragonbra.javasteam.types.FileData;
import in.dragonbra.javasteam.types.KeyValue;
import kotlinx.coroutines.GlobalScope;
import kotlinx.coroutines.future.FutureKt;

/**
 * The game from Steam, over SteamLink's signed-in connection (FF7Recomp's downloader).
 *
 * FINAL FANTASY IX (app 377840): the files the folder import keeps (everything but the x86
 * build, programs, the Mono runtime and Windows plugins) come from the account's Windows
 * depots, each checked against Steam's SHA-1. Files already here and intact are kept, so a
 * download stopped half way goes on where it was, and running it again repairs damaged files.
 * Then Memoria (MemoriaInstall) goes on top; the files Memoria replaces are not taken from Steam.
 */
final class SteamFiles {
    private static final String TAG = "FF9SteamFiles";

    static {
        // JavaSteam decrypts depot chunks with the provider named "BC" and loads
        // org.bouncycastle's class for it. Android's own "BC" lacks AES since Android 9, so the
        // full BouncyCastle (bcprov) takes the name.
        try {
            java.security.Security.removeProvider("BC");
            java.security.Security.addProvider(new org.bouncycastle.jce.provider.BouncyCastleProvider());
        } catch (Throwable e) {
            Log.w(TAG, "BouncyCastle", e);
        }
    }

    interface Progress { void update(String what); }

    static final String NOT_OWNED = "this Steam account does not own FINAL FANTASY IX";

    private final SteamLink link;

    SteamFiles(SteamLink link) { this.link = link; }

    /** A file of the depot and where it goes under the game folder. */
    private static final class Want { FileData f; String to; int depot; }

    /**
     * Downloads the game into game, then installs Memoria. Returns null when done, else what
     * stopped it ("stopped" when cancel was set; what was done stays).
     */
    String downloadGame(GameInstall install, File game, Progress p, AtomicBoolean cancel) throws Exception {
        MemoriaInstall memoria = new MemoriaInstall(install);
        p.update("Downloading Memoria " + MemoriaInstall.VERSION);
        File exe;
        try { exe = memoria.fetch(p::update, cancel); }
        catch (InterruptedException e) { return "stopped"; }
        Set<String> memoriaFiles = MemoriaInstall.paths(exe);

        p.update("Connecting to Steam");
        try (SteamLink.Session s = link.open()) {
            int app = SteamLink.APP_FF9;
            if (!SteamLink.owns(s, app)) return NOT_OWNED;
            p.update("Reading the game's file list from Steam");
            KeyValue depots = appInfo(s, app).get("depots");
            SteamApps apps = s.client.getHandler(SteamApps.class);
            SteamContent content = s.client.getHandler(SteamContent.class);
            List<Server> servers = new ArrayList<>();
            for (Server sv : FutureKt.asCompletableFuture(content.getServersForSteamPipe(s.cell, 20, GlobalScope.INSTANCE)).get(30, TimeUnit.SECONDS))
                if ("CDN".equals(sv.getType()) || "SteamCache".equals(sv.getType())) servers.add(sv);
            if (servers.isEmpty()) throw new IOException("Steam named no download server");

            Map<Integer, byte[]> keys = new HashMap<>();
            Map<String, Want> wants = new LinkedHashMap<>();
            try (in.dragonbra.javasteam.steam.cdn.Client cdn = new in.dragonbra.javasteam.steam.cdn.Client(s.client)) {
                for (KeyValue d : depots.getChildren()) {
                    int id;
                    try { id = Integer.parseInt(d.getName()); } catch (NumberFormatException e) { continue; }
                    String os = d.get("config").get("oslist").getValue(), lang = d.get("config").get("language").getValue();
                    KeyValue pub = d.get("manifests").get("public");
                    String gid = pub.get("gid").getValue() != null ? pub.get("gid").getValue() : pub.getValue();
                    Log.i(TAG, "depot " + id + " os=" + os + " lang=" + lang + " manifest=" + gid);
                    if (os != null && !os.isEmpty() && !os.contains("windows")) continue;
                    if (lang != null && !lang.isEmpty() && !lang.equals("english")) continue;
                    if (gid == null) continue;                              // shared redistributables
                    long manifestId = Long.parseUnsignedLong(gid);
                    DepotKeyCallback k = apps.getDepotDecryptionKey(id, app).toFuture().get(30, TimeUnit.SECONDS);
                    if (k.getResult() != EResult.OK) { Log.w(TAG, "depot " + id + " key: " + k.getResult()); continue; }
                    long code = FutureKt.asCompletableFuture(content.getManifestRequestCode(id, app, manifestId, "public", null, GlobalScope.INSTANCE))
                            .get(30, TimeUnit.SECONDS);
                    DepotManifest m = null;
                    Exception last = null;
                    for (int t = 0; t < Math.min(4, servers.size()) && m == null; t++) {
                        try { m = cdn.downloadManifestFuture(id, manifestId, code, servers.get(t), k.getDepotKey()).get(120, TimeUnit.SECONDS); }
                        catch (Exception e) { last = e; }
                    }
                    if (m == null) throw last;
                    if (m.getFilenamesEncrypted()) m.decryptFilenames(k.getDepotKey());
                    keys.put(id, k.getDepotKey());
                    // the game folder inside the depot: the one holding x64/FF9_Data/mainData
                    String root = null;
                    for (FileData f : m.getFiles()) {
                        String l = f.getFileName().replace('\\', '/').toLowerCase(Locale.ROOT);
                        if (l.equals("x64/ff9_data/maindata")) { root = ""; break; }
                        if (l.endsWith("/x64/ff9_data/maindata")) root = l.substring(0, l.length() - "x64/ff9_data/maindata".length());
                    }
                    if (root == null) root = "";
                    int taken = 0;
                    for (FileData f : m.getFiles()) {
                        if (f.getFlags().contains(EDepotFileFlag.Directory)) continue;
                        String n = f.getFileName().replace('\\', '/');
                        if (!n.toLowerCase(Locale.ROOT).startsWith(root)) continue;
                        String rel = n.substring(root.length());
                        String low = rel.toLowerCase(Locale.ROOT);
                        if (rel.isEmpty() || GameInstall.skipped(rel) || memoriaFiles.contains(low) || wants.containsKey(low)) continue;
                        Want w = new Want();
                        w.f = f;
                        w.to = rel;
                        w.depot = id;
                        wants.put(low, w);
                        taken++;
                    }
                    Log.i(TAG, "depot " + id + ": " + m.getFiles().size() + " entries, root '" + root + "', " + taken + " taken");
                }
                if (!wants.containsKey("x64/ff9_data/maindata")) throw new IOException("Steam's copy of the game has no x64/FF9_Data/mainData");

                // what is already here and intact stays
                game.mkdirs();
                List<Want> todo = new ArrayList<>();
                long total = 0, have = 0, need = 0;
                int checked = 0;
                for (Want w : wants.values()) {
                    if (cancel.get()) return "stopped";
                    if ((++checked & 31) == 0) p.update("Checking the files already here: " + checked + " of " + wants.size());
                    File dst = new File(game, w.to);
                    total += w.f.getTotalSize();
                    if (dst.isFile() && dst.length() == w.f.getTotalSize() && Arrays.equals(sha1(dst), w.f.getFileHash())) {
                        have += w.f.getTotalSize();
                        continue;
                    }
                    need += w.f.getTotalSize() - (dst.isFile() ? dst.length() : 0);
                    todo.add(w);
                }
                Log.i(TAG, wants.size() + " files, " + todo.size() + " to download, " + (total - have) + " bytes");
                long free = game.getUsableSpace();
                if (need + (256L << 20) > free)
                    throw new IOException("not enough free space: the download needs " + gb(need + (256L << 20)) + " GB more and "
                            + gb(free) + " GB is free");

                AtomicLong done = new AtomicLong(have);
                AtomicInteger next = new AtomicInteger(), failed = new AtomicInteger();
                final List<String> errors = Collections.synchronizedList(new ArrayList<>());
                final long tot = total;
                ExecutorService pool = Executors.newFixedThreadPool(8);
                List<Future<?>> jobs = new ArrayList<>();
                for (Want w : todo) {
                    jobs.add(pool.submit(() -> {
                        if (cancel.get() || failed.get() > 0) return;
                        try {
                            fetch(cdn, servers, next, w.depot, keys.get(w.depot), w, new File(game, w.to), done, cancel);
                        } catch (Exception e) {
                            Log.e(TAG, "download " + w.to, e);
                            errors.add(w.to + ": " + SteamLink.message(e));
                            failed.incrementAndGet();
                        }
                    }));
                }
                pool.shutdown();
                while (!pool.awaitTermination(500, TimeUnit.MILLISECONDS)) {
                    if (tot > 0) JobService.percent = (int) (done.get() * 100 / tot);
                    p.update("Downloading from Steam: " + gb(done.get()) + " of " + gb(tot) + " GB");
                }
                if (cancel.get()) return "stopped";
                if (!errors.isEmpty()) throw new IOException(errors.get(0));
            }
        }
        p.update("Installing Memoria " + MemoriaInstall.VERSION);
        memoria.install(game, p::update);
        if (game.equals(install.game)) install.configureMemoria();      // the SoLoud backend
        else configureCopy(game);
        return null;
    }

    /** The Android settings for a test download outside the app's game folder. */
    private static void configureCopy(File game) throws IOException {
        File ini = new File(game, "Memoria.ini");
        if (!ini.isFile()) return;
        String t = new String(java.nio.file.Files.readAllBytes(ini.toPath()), java.nio.charset.StandardCharsets.ISO_8859_1);
        t = t.replaceAll("(?m)^Backend\\s*=\\s*\\d+", "Backend = 2");
        java.nio.file.Files.write(ini.toPath(), t.getBytes(java.nio.charset.StandardCharsets.ISO_8859_1));
    }

    private static String gb(long b) { return String.format(Locale.ROOT, "%.2f", b / 1e9); }

    /** One file: its chunks, each from the next server (another on failure), then its SHA-1. */
    private static void fetch(in.dragonbra.javasteam.steam.cdn.Client cdn, List<Server> servers, AtomicInteger next, int depot, byte[] key,
                              Want w, File dst, AtomicLong done, AtomicBoolean cancel) throws Exception {
        File part = new File(dst.getPath() + ".part");
        dst.getParentFile().mkdirs();
        long got = 0;
        try (RandomAccessFile raf = new RandomAccessFile(part, "rw")) {
            raf.setLength(w.f.getTotalSize());
            for (ChunkData c : w.f.getChunks()) {
                if (cancel.get()) return;
                // JavaSteam 1.8.0 wants room for the compressed chunk when a depot key is given
                // (a small file that does not compress is bigger compressed)
                byte[] buf = new byte[Math.max(c.getUncompressedLength(), c.getCompressedLength())];
                int n = -1;
                Exception last = null;
                for (int t = 0; t < 6 && n < 0; t++) {
                    Server sv = servers.get(Math.floorMod(next.getAndIncrement(), servers.size()));
                    try { n = cdn.downloadDepotChunkFuture(depot, c, sv, buf, key).get(120, TimeUnit.SECONDS); }
                    catch (Exception e) { last = e; Thread.sleep(500L * (t + 1)); }
                }
                if (n < 0) throw last;
                raf.seek(c.getOffset());
                raf.write(buf, 0, n);
                done.addAndGet(n);
                got += n;
            }
        } catch (Exception e) {
            done.addAndGet(-got);
            throw e;
        }
        if (cancel.get()) return;
        if (!Arrays.equals(sha1(part), w.f.getFileHash())) {
            part.delete();
            done.addAndGet(-got);
            throw new IOException("the downloaded file did not match Steam's checksum");
        }
        if (dst.exists() && !dst.delete()) throw new IOException("cannot replace " + dst);
        if (!part.renameTo(dst)) throw new IOException("cannot write " + dst);
    }

    // ================================================================== Steam Cloud saves

    /*
     * FF9 has a built in Cloud data storage where it keeps a copy of the save files separate from the main game saves.
     * Steam watches this folder for changes then uploads the modified files to Steam Cloud.
     * FF9_CloudSaveFile_ww.sav (_jp for the Japanese game).
     * The PC game writes it to <My Documents>\Square Enix\FINAL FANTASY IX.
     * Here the engine points the game at cloudDir (Native/Compat.cs's FF9_CLOUD_DIR)
     * syncCloud keeps that folder in step with Steam Cloud, before and after play.
     * A file changed on one side only goes to the other. If it changed on both sides, the newer one is kept.
     * On the first sync, Steam's copy is kept so the PC's Cloud save is never overwritten by surprise.
     * Any copy about to be replaced is saved to steam/cloud-backup first.
     * Other than FF9 unique process, a lot of this was lifted from the FF7 Recomp project.
     */

    /** Matches the Cloud menu's file names, in lower case. */
    private static final java.util.regex.Pattern CLOUD_FILE = java.util.regex.Pattern.compile("ff9_cloudsavefile_(ww|jp)\\.sav");
    /** The folder the files are in on Steam Cloud, after the My Documents root. */
    private static final String CLOUD_FOLDER = "square enix/final fantasy ix/";
    /** Steam Cloud's name for the My Documents root. Only used when uploading a file Steam doesn't have yet. */
    private static final String CLOUD_ROOT = "%WinMyDocuments%";

    /** The folder the game's Cloud menu reads and writes on Android. GameActivity passes it to the game as FF9_CLOUD_DIR. */
    static File cloudDir(android.content.Context c) { return new File(new GameInstall(c).files, "steam/cloud"); }

    /** The result of a sync, shown in the launcher. */
    static final class CloudResult {
        final List<String> up = new ArrayList<>(), down = new ArrayList<>(), conflicts = new ArrayList<>();
        boolean changed() { return !up.isEmpty() || !down.isEmpty(); }
        @Override public String toString() {
            if (!changed()) return "your Cloud save and Steam's match.";
            StringBuilder sb = new StringBuilder();
            if (!down.isEmpty()) sb.append("Steam's Cloud save is here: use Download on the title screen to load it. ");
            if (!up.isEmpty()) sb.append("Your Cloud save went to Steam. ");
            if (!conflicts.isEmpty()) sb.append("It had changed on both sides; the ").append(down.isEmpty() ? "one here" : "one on Steam")
                    .append(" was kept and the other is in steam/cloud-backup.");
            return sb.toString().trim();
        }
    }

    /** Syncs the Cloud menu's folder with FINAL FANTASY IX's Steam Cloud in both directions. */
    CloudResult syncCloud(File dir) throws Exception {
        File state = new File(link.dir, "cloud-sync.txt");
        CloudResult r = new CloudResult();
        try (SteamLink.Session s = link.open()) {
            int app = SteamLink.APP_FF9;
            if (!SteamLink.owns(s, app)) throw new IOException(NOT_OWNED);

            // Collect Steam's copies by lower-case name, along with each file's full Steam Cloud name.
            AppFileChangeList l = cloud(s).getAppFileListChange(app).get(60, TimeUnit.SECONDS);
            Map<String, AppFileInfo> remote = new HashMap<>();
            Map<String, String> remoteName = new HashMap<>();
            for (AppFileInfo f : l.getFiles()) {
                String prefix = f.getPathPrefixIndex() < l.getPathPrefixes().size() ? l.getPathPrefixes().get(f.getPathPrefixIndex()) : "";
                String full = prefix + f.getFilename();
                String low = full.replace('\\', '/').toLowerCase(Locale.ROOT);
                String name = low.substring(low.lastIndexOf('/') + 1);
                if (!CLOUD_FILE.matcher(name).matches() || !low.endsWith(CLOUD_FOLDER + name)) continue;
                if (f.getPersistState() == in.dragonbra.javasteam.protobufs.steamclient.Enums.ECloudStoragePersistState.k_ECloudStoragePersistStateDeleted) continue;
                remote.put(name, f);
                remoteName.put(name, full);
            }
            Log.i(TAG, "cloud: " + remoteName.values() + " (" + l.getFiles().size() + " files in all)");

            Map<String, String> synced = readState(state);
            Map<String, File> local = new HashMap<>();
            String[] here = dir.list();
            if (here != null) for (String n : here) if (CLOUD_FILE.matcher(n.toLowerCase(Locale.ROOT)).matches()) local.put(n.toLowerCase(Locale.ROOT), new File(dir, n));
            java.util.TreeSet<String> names = new java.util.TreeSet<>(remote.keySet());
            names.addAll(local.keySet());

            List<String> upload = new ArrayList<>();
            for (String n : names) {
                File f = local.get(n);
                if (f == null) f = new File(dir, gameName(n));
                String lsha = f.isFile() ? hex(sha1(f)) : null;
                AppFileInfo rf = remote.get(n);
                String rsha = rf != null ? hex(rf.getShaFile()) : null;
                String was = synced.get(n);
                if (lsha != null && lsha.equals(rsha)) { synced.put(n, lsha); continue; }
                boolean localChanged = lsha != null && !lsha.equals(was), remoteChanged = rsha != null && !rsha.equals(was);
                boolean takeRemote;
                if (rsha == null) takeRemote = false;
                else if (lsha == null) takeRemote = true;
                else if (remoteChanged && !localChanged) takeRemote = true;
                else if (localChanged && !remoteChanged) takeRemote = false;
                else {                                    // Changed on both sides. Keep the newer one, or Steam's on the first sync.
                    takeRemote = was == null || rf.getTimestamp().getTime() > f.lastModified();
                    r.conflicts.add(n);
                    if (!takeRemote) backup("steam-" + gameName(n), downloadCloud(s, app, remoteName.get(n)));
                }
                if (takeRemote) {
                    byte[] b = downloadCloud(s, app, remoteName.get(n));
                    if (!hex(sha1(b)).equals(rsha)) throw new IOException("Steam Cloud's " + gameName(n) + " did not match its checksum");
                    if (f.isFile()) backup(f.getName(), java.nio.file.Files.readAllBytes(f.toPath()));
                    dir.mkdirs();
                    File tmp = new File(dir, f.getName() + ".part");
                    java.nio.file.Files.write(tmp.toPath(), b);
                    if (f.exists() && !f.delete()) throw new IOException("cannot replace " + f);
                    if (!tmp.renameTo(f)) throw new IOException("cannot write " + f);
                    f.setLastModified(rf.getTimestamp().getTime());
                    synced.put(n, rsha);
                    r.down.add(n);
                } else {
                    upload.add(n);
                }
            }

            if (!upload.isEmpty()) {
                List<String> full = new ArrayList<>();
                for (String n : upload) full.add(remoteName.containsKey(n) ? remoteName.get(n) : CLOUD_ROOT + "Square Enix/FINAL FANTASY IX/" + gameName(n));
                SteamCloud c = cloud(s);
                long batch = c.beginAppUploadBatch(app, "FF9Recomp on " + android.os.Build.MODEL, full, Collections.emptyList(), clientId(), 0L)
                        .get(60, TimeUnit.SECONDS).getBatchID();
                EResult result = EResult.Fail;
                try {
                    for (int i = 0; i < upload.size(); i++) {
                        String n = upload.get(i);
                        File f = local.get(n);
                        byte[] b = java.nio.file.Files.readAllBytes(f.toPath());
                        byte[] sha = sha1(b);
                        FileUploadInfo u = c.beginFileUpload(app, b.length, b.length, sha, new java.util.Date(f.lastModified()),
                                full.get(i), -1, s.cell, false, false, null, batch, GlobalScope.INSTANCE).get(60, TimeUnit.SECONDS);
                        boolean ok = true;
                        for (FileUploadBlockDetails blk : u.getBlockRequests()) ok &= put(blk, b);
                        if (!c.commitFileUpload(ok, app, sha, full.get(i)).get(60, TimeUnit.SECONDS) || !ok)
                            throw new IOException("Steam Cloud did not take " + gameName(n));
                        synced.put(n, hex(sha));
                        r.up.add(n);
                    }
                    result = EResult.OK;
                } finally {
                    try { c.completeAppUploadBatch(app, batch, result).get(60, TimeUnit.SECONDS); } catch (Exception e) { Log.w(TAG, "complete batch", e); }
                }
            }
            writeState(state, synced);
        }
        Log.i(TAG, "cloud sync: down " + r.down + ", up " + r.up + ", conflicts " + r.conflicts);
        return r;
    }

    /** Lists FINAL FANTASY IX's Steam Cloud files, one per line. Used by the adb test API. */
    String cloudListing() throws Exception {
        StringBuilder sb = new StringBuilder();
        try (SteamLink.Session s = link.open()) {
            AppFileChangeList l = cloud(s).getAppFileListChange(SteamLink.APP_FF9).get(60, TimeUnit.SECONDS);
            sb.append("change ").append(l.getCurrentChangeNumber()).append(" prefixes ").append(l.getPathPrefixes()).append('\n');
            for (AppFileInfo f : l.getFiles())
                sb.append(f.getPathPrefixIndex()).append(" | ").append(f.getFilename()).append(" | ").append(f.getRawFileSize())
                  .append(" | ").append(f.getTimestamp()).append(" | ").append(f.getPersistState()).append(" | ").append(hex(f.getShaFile())).append('\n');
        }
        return sb.toString();
    }

    /** Turns a lower-case name back into the name the game uses, for example FF9_CloudSaveFile_ww.sav. */
    private static String gameName(String low) { return "FF9_CloudSaveFile_" + low.substring("ff9_cloudsavefile_".length()); }

    private static SteamCloud cloud(SteamLink.Session s) { return s.client.getHandler(SteamCloud.class); }

    /** A random id for this device that stays the same between uploads. Steam asks for one with each upload. */
    private long clientId() {
        android.content.SharedPreferences p = link.prefs();
        long id = p.getLong("cloud_client", 0);
        if (id == 0) { id = new java.security.SecureRandom().nextLong() & Long.MAX_VALUE; p.edit().putLong("cloud_client", id).apply(); }
        return id;
    }

    /** Downloads one Steam Cloud file. Unzips it if Steam stored it compressed. */
    private static byte[] downloadCloud(SteamLink.Session s, int app, String name) throws Exception {
        FileDownloadInfo d = cloud(s).clientFileDownload(app, name).get(60, TimeUnit.SECONDS);
        if (d.getUrlHost() == null || d.getUrlHost().isEmpty()) throw new IOException("Steam gave no download address for " + name);
        java.net.HttpURLConnection c = (java.net.HttpURLConnection) new java.net.URL((d.getUseHttps() ? "https://" : "http://") + d.getUrlHost() + d.getUrlPath()).openConnection();
        c.setConnectTimeout(30000);
        c.setReadTimeout(60000);
        for (HttpHeaders h : d.getRequestHeaders()) c.setRequestProperty(h.getName(), h.getValue());
        int code = c.getResponseCode();
        if (code != 200) throw new IOException("Steam Cloud answered HTTP " + code + " for " + name);
        byte[] b;
        try (InputStream in = c.getInputStream()) { b = readAll(in); }
        if (d.getRawFileSize() != d.getFileSize() && b.length >= 4 && b[0] == 'P' && b[1] == 'K') {
            try (java.util.zip.ZipInputStream z = new java.util.zip.ZipInputStream(new java.io.ByteArrayInputStream(b))) {
                if (z.getNextEntry() != null) b = readAll(z);
            }
        }
        return b;
    }

    /** Uploads one block of a file the way Steam asks for it. Returns true if the server accepted it. */
    private static boolean put(FileUploadBlockDetails blk, byte[] data) throws IOException {
        String[] methods = { "GET", "GET", "HEAD", "POST", "PUT", "DELETE" };
        String method = blk.getHttpMethod() >= 0 && blk.getHttpMethod() < methods.length ? methods[blk.getHttpMethod()] : "PUT";
        java.net.HttpURLConnection c = (java.net.HttpURLConnection) new java.net.URL((blk.getUseHttps() ? "https://" : "http://") + blk.getUrlHost() + blk.getUrlPath()).openConnection();
        c.setConnectTimeout(30000);
        c.setReadTimeout(60000);
        c.setRequestMethod(method);
        for (HttpHeaders h : blk.getRequestHeaders()) c.setRequestProperty(h.getName(), h.getValue());
        byte[] body = blk.getExplicitBodyData() != null && blk.getExplicitBodyData().length > 0 ? blk.getExplicitBodyData()
                : Arrays.copyOfRange(data, (int) blk.getBlockOffset(), (int) blk.getBlockOffset() + blk.getBlockLength());
        c.setDoOutput(true);
        c.setFixedLengthStreamingMode(body.length);
        try (java.io.OutputStream o = c.getOutputStream()) { o.write(body); }
        int code = c.getResponseCode();
        if (code < 200 || code >= 300) Log.w(TAG, "upload block: HTTP " + code);
        return code >= 200 && code < 300;
    }

    /** Saves a copy about to be replaced to steam/cloud-backup, with the time in its name. */
    private void backup(String name, byte[] b) throws IOException {
        File d = new File(link.dir, "cloud-backup");
        d.mkdirs();
        String stamp = new java.text.SimpleDateFormat("yyyyMMdd-HHmmss", Locale.ROOT).format(new java.util.Date());
        java.nio.file.Files.write(new File(d, stamp + "-" + name).toPath(), b);
    }

    /** Reads each file's SHA-1 from the last time both sides matched. The file has one "name sha" line per file. */
    private static Map<String, String> readState(File f) {
        Map<String, String> m = new HashMap<>();
        if (!f.isFile()) return m;
        try {
            for (String l : new String(java.nio.file.Files.readAllBytes(f.toPath()), java.nio.charset.StandardCharsets.UTF_8).split("\n")) {
                String[] p = l.trim().split(" ");
                if (p.length == 2) m.put(p[0], p[1]);
            }
        } catch (IOException e) {
            Log.w(TAG, "cloud state", e);
        }
        return m;
    }

    private static void writeState(File f, Map<String, String> m) throws IOException {
        StringBuilder sb = new StringBuilder();
        for (Map.Entry<String, String> e : new java.util.TreeMap<>(m).entrySet()) sb.append(e.getKey()).append(' ').append(e.getValue()).append('\n');
        f.getParentFile().mkdirs();
        java.nio.file.Files.write(f.toPath(), sb.toString().getBytes(java.nio.charset.StandardCharsets.UTF_8));
    }

    private static byte[] readAll(InputStream in) throws IOException {
        java.io.ByteArrayOutputStream o = new java.io.ByteArrayOutputStream();
        byte[] buf = new byte[1 << 16];
        for (int n; (n = in.read(buf)) > 0; ) o.write(buf, 0, n);
        return o.toByteArray();
    }

    private static byte[] sha1(byte[] b) {
        try { return MessageDigest.getInstance("SHA-1").digest(b); }
        catch (java.security.NoSuchAlgorithmException e) { throw new IllegalStateException(e); }
    }

    private static String hex(byte[] b) {
        if (b == null) return null;
        StringBuilder sb = new StringBuilder();
        for (byte x : b) sb.append(String.format(Locale.ROOT, "%02x", x));
        return sb.toString();
    }

    static KeyValue appInfo(SteamLink.Session s, int app) throws Exception {
        SteamApps apps = s.client.getHandler(SteamApps.class);
        PICSTokensCallback t = apps.picsGetAccessTokens(app, null).toFuture().get(30, TimeUnit.SECONDS);
        Long token = t.getAppTokens().get(app);
        PICSRequest req = new PICSRequest(app, token != null ? token : 0L);
        List<PICSProductInfoCallback> res = apps.picsGetProductInfo(Collections.singletonList(req), Collections.emptyList())
                .toFuture().get(60, TimeUnit.SECONDS).getResults();
        for (PICSProductInfoCallback cb : res) {
            PICSProductInfo pi = cb.getApps().get(app);
            if (pi != null) return pi.getKeyValues();
        }
        throw new IOException("Steam returned no details for app " + app);
    }

    static byte[] sha1(File f) throws IOException {
        try (InputStream in = new FileInputStream(f)) {
            MessageDigest md = MessageDigest.getInstance("SHA-1");
            byte[] buf = new byte[1 << 20];
            for (int n; (n = in.read(buf)) > 0; ) md.update(buf, 0, n);
            return md.digest();
        } catch (java.security.NoSuchAlgorithmException e) {
            throw new IOException(e);
        }
    }
}
