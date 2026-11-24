package com.ff9recomp;

import android.content.Context;
import android.util.Xml;

import org.xmlpull.v1.XmlPullParser;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * Memoria's mod catalog (the list Memoria's own launcher shows): fetched from Memoria's GitHub,
 * kept in files/mods/catalog.xml for offline use. Mods are installed into the imported game's
 * folder (game/<InstallationPath>) and turned on by listing them in Memoria.ini's FolderNames,
 * highest priority first, as Memoria's launcher does. The FF7Recomp mod manager's idea, for
 * Memoria's catalog.
 */
final class ModCatalog {
    static final String URL_CATALOG = "https://raw.githubusercontent.com/Albeoris/Memoria/mod-catalog/MemoriaCatalog.xml";

    static final class Mod {
        String name = "", version = "", folder = "", author = "", description = "", category = "", url = "", website = "", format = "";
        String releaseDate = "", patchNotes = "", preview = "", minMemoria = "";
        final List<String> incompatible = new ArrayList<>();
        /** The mod's options (Memoria's sub-mods) and the headers between them, in order. */
        final List<SubMod> subMods = new ArrayList<>();
        int priority;

        /** The download size when the patch notes give one ("Download Size: 3,2GB"), else "". */
        String sizeText() {
            java.util.regex.Matcher mt = java.util.regex.Pattern.compile("(?i)download size\\s*:\\s*([0-9.,]+\\s*[KMG]B)").matcher(patchNotes);
            return mt.find() ? mt.group(1).replace(" ", "") : "";
        }
    }

    /** One of a mod's options: a folder inside the mod that Memoria loads when it is chosen (a
     *  "sub-mod"), or a header that only titles the options below it. Options in the same group
     *  exclude each other; ActivateWithMod / ActivateWithoutMod ones follow other mods by
     *  themselves and are not shown. As Memoria.Launcher's Mod.cs reads them. */
    static final class SubMod {
        String name = "", path = "", description = "", group;
        boolean header, isDefault;
        List<String> withMod, withoutMod;
        int priority;
        boolean auto() { return withMod != null || withoutMod != null; }
        boolean choice() { return !header && !auto(); }
    }

    private ModCatalog() {}

    // the storage paths and the parsed catalog, cached: finding the app's storage asks Android
    // each time, and the Get mods page asks for every mod (it took seconds to draw)
    private static File filesDir, gameDir;
    private static long pathsAt = -1;
    private static List<Mod> parsed;
    private static long parsedStamp;

    private static synchronized void paths(Context c) {
        long now = android.os.SystemClock.uptimeMillis();
        if (pathsAt < 0 || now - pathsAt > 2000) {
            GameInstall gi = new GameInstall(c);
            filesDir = gi.files;
            gameDir = gi.game;
            pathsAt = now;
        }
    }

    static File game(Context c) { paths(c); return gameDir; }
    static File dir(Context c) { paths(c); return new File(filesDir, "mods"); }
    static File cached(Context c) { return new File(dir(c), "catalog.xml"); }

    /** Downloads the catalog (on the caller's thread). */
    static void refresh(Context c) throws IOException {
        HttpURLConnection h = (HttpURLConnection) new URL(URL_CATALOG).openConnection();
        h.setRequestProperty("User-Agent", "FF9Recomp/" + Updater.installedVersion(c));
        h.setConnectTimeout(15000);
        h.setReadTimeout(30000);
        if (h.getResponseCode() != 200) throw new IOException("the catalog answered " + h.getResponseCode());
        dir(c).mkdirs();
        File tmp = new File(dir(c), "catalog.tmp");
        try (InputStream in = h.getInputStream(); OutputStream o = new FileOutputStream(tmp)) {
            byte[] b = new byte[1 << 16];
            for (int n; (n = in.read(b)) > 0; ) o.write(b, 0, n);
        }
        if (parse(tmp).isEmpty()) throw new IOException("the catalog came back empty");
        if (!tmp.renameTo(cached(c))) throw new IOException("cannot save the catalog");
    }

    /** The cached catalog's mods (empty when it was never fetched). */
    static synchronized List<Mod> mods(Context c) {
        File f = cached(c);
        long stamp = f.isFile() ? f.lastModified() ^ (f.length() << 20) : 0;
        if (parsed == null || stamp != parsedStamp) {
            try { parsed = parse(f); } catch (Exception e) { parsed = new ArrayList<>(); }
            parsedStamp = stamp;
        }
        return new ArrayList<>(parsed);
    }

    private static List<Mod> parse(File f) throws IOException { return parse(f, true); }

    /** The mods in a catalog, or (catalog false) the one in a mod's own ModDescription.xml, which
     *  has no download link. */
    private static List<Mod> parse(File f, boolean catalog) throws IOException {
        List<Mod> out = new ArrayList<>();
        if (!f.isFile()) return out;
        try (InputStream in = new FileInputStream(f)) {
            XmlPullParser p = Xml.newPullParser();
            p.setInput(in, "UTF-8");
            Mod m = null;
            SubMod s = null;
            int depth = 0, modDepth = -1;
            String tag = null;
            for (int ev = p.getEventType(); ev != XmlPullParser.END_DOCUMENT; ev = p.next()) {
                if (ev == XmlPullParser.START_TAG) {
                    depth++;
                    String name = p.getName();
                    if (name.equals("Mod") && m == null) { m = new Mod(); modDepth = depth; }
                    else if (m != null && s == null && depth == modDepth + 1 && (name.equals("SubMod") || name.equals("Header"))) {
                        s = new SubMod();
                        s.header = name.equals("Header");
                    } else if (s != null && !s.header && depth == modDepth + 2 && name.equals("Default")) s.isDefault = true;
                    tag = name;
                } else if (ev == XmlPullParser.END_TAG) {
                    if (s != null && depth == modDepth + 1) {
                        // as Memoria: a header needs a title, an option a name and a folder
                        if (s.header ? !s.name.isEmpty() : !s.name.isEmpty() && !s.path.isEmpty()) m.subMods.add(s);
                        s = null;
                    } else if (m != null && depth == modDepth && p.getName().equals("Mod")) {
                        if (!catalog || !m.folder.isEmpty() && !m.url.isEmpty()) out.add(m);
                        m = null;
                    }
                    depth--;
                    tag = null;
                } else if (ev == XmlPullParser.TEXT && s != null && tag != null) {
                    String t = p.getText().trim();
                    if (s.header && depth == modDepth + 1) s.name = t;
                    else if (!s.header && depth == modDepth + 2) {
                        switch (tag) {
                            case "Name": s.name = t; break;
                            case "InstallationPath": s.path = t; break;
                            case "Description": s.description = t; break;
                            case "Group": s.group = t; break;
                            case "ActivateWithMod": s.withMod = names(t); s.withoutMod = null; break;   // wins, as in Memoria
                            case "ActivateWithoutMod": if (s.withMod == null) s.withoutMod = names(t); break;
                            case "Priority": try { s.priority = Integer.parseInt(t); } catch (NumberFormatException ignored) { } break;
                        }
                    }
                } else if (ev == XmlPullParser.TEXT && m != null && tag != null && depth == modDepth + 1) {
                    String t = p.getText().trim();
                    switch (tag) {
                        case "Name": m.name = t; break;
                        case "Version": m.version = t; break;
                        case "InstallationPath": m.folder = t; break;
                        case "Author": m.author = t; break;
                        case "Description": m.description = t; break;
                        case "Category": m.category = t; break;
                        case "DownloadUrl": m.url = t; break;
                        case "Website": m.website = t; break;
                        case "DownloadFormat": m.format = t; break;
                        case "ReleaseDate": m.releaseDate = t; break;
                        case "PatchNotes": m.patchNotes = t; break;
                        case "PreviewFileUrl": m.preview = t; break;
                        case "MinimumMemoriaVersion": m.minMemoria = t; break;
                        case "IncompatibleWith": for (String x : t.split(",")) if (!x.trim().isEmpty()) m.incompatible.add(x.trim()); break;
                        case "Priority": try { m.priority = Integer.parseInt(t); } catch (NumberFormatException ignored) { } break;
                    }
                }
            }
        } catch (org.xmlpull.v1.XmlPullParserException e) {
            throw new IOException("the catalog could not be read: " + e.getMessage());
        }
        return out;
    }

    private static List<String> names(String list) {
        List<String> out = new ArrayList<>();
        for (String x : list.split(",")) out.add(x.trim());
        return out;
    }

    /** How old the cached catalog is, in ms (Long.MAX_VALUE when it was never fetched). */
    static long age(Context c) {
        File f = cached(c);
        return f.isFile() ? System.currentTimeMillis() - f.lastModified() : Long.MAX_VALUE;
    }

    /** The categories that have mods, in the catalog's order. */
    static List<String> categories(List<Mod> all) {
        List<String> out = new ArrayList<>();
        for (Mod m : all) { String k = m.category.isEmpty() ? "Other" : m.category; if (!out.contains(k)) out.add(k); }
        return out;
    }

    /** Every word of query in the name, author, category or description; category null for all. */
    static List<Mod> search(Context c, List<Mod> all, String query, String category, boolean installedOnly) {
        String[] words = query == null ? new String[0] : query.toLowerCase(java.util.Locale.ROOT).trim().split("\\s+");
        List<Mod> out = new ArrayList<>();
        for (Mod m : all) {
            if (installedOnly && !installed(c, m)) continue;
            if (category != null && !category.equals(m.category.isEmpty() ? "Other" : m.category)) continue;
            String hay = (m.name + " " + m.author + " " + m.category + " " + m.description + " " + m.folder).toLowerCase(java.util.Locale.ROOT);
            boolean ok = true;
            for (String w : words) if (!w.isEmpty() && !hay.contains(w)) { ok = false; break; }
            if (ok) out.add(m);
        }
        return out;
    }

    static Mod byFolder(Context c, String folder) {
        for (Mod m : mods(c)) if (m.folder.equalsIgnoreCase(folder)) return m;
        return null;
    }

    /** Catalog mods with an update. */
    static List<Mod> updates(Context c) {
        List<Mod> out = new ArrayList<>();
        Set<String> names = gameNames(c);
        for (Mod m : mods(c)) if (maybeInstalled(names, m) && updatable(c, m)) out.add(m);
        return out;
    }

    /** The mod folders in the game folder: catalog mods that are installed, then any other
     *  folder Memoria.ini lists (added by hand on the PC). */
    static List<String> installedFolders(Context c) {
        List<String> out = new ArrayList<>();
        for (String f : enabled(c)) if (new File(game(c), f).isDirectory()) out.add(f);
        Set<String> names = gameNames(c);
        for (Mod m : mods(c)) if (maybeInstalled(names, m) && installed(c, m) && !containsIgnoreCase(out, m.folder)) out.add(m.folder);
        // a mod copied in by hand (GitHub issue 9): a folder laid out as Memoria's mods are
        File[] dirs = game(c).listFiles(File::isDirectory);
        if (dirs != null) {
            java.util.Arrays.sort(dirs);
            for (File d : dirs) if (looksLikeMod(d) && !containsIgnoreCase(out, d.getName())) out.add(d.getName());
        }
        return out;
    }

    private static final List<String> GAME_DIRS = java.util.Arrays.asList(
            "StreamingAssets", "FF9_Data", "x64", "x86", "SaveData", "cache", "persistent", "Debugger", "Licenses");

    /** A mod folder: it has a ModDescription.xml, or the game's own folders to put files over. */
    private static boolean looksLikeMod(File d) {
        if (containsIgnoreCase(GAME_DIRS, d.getName()) || d.getName().startsWith(".")) return false;
        return new File(d, "ModDescription.xml").isFile() || new File(d, "StreamingAssets").isDirectory()
                || new File(d, "FF9_Data").isDirectory();
    }

    private static boolean containsIgnoreCase(List<String> l, String s) {
        for (String x : l) if (x.equalsIgnoreCase(s)) return true;
        return false;
    }

    // ------------------------------------------------------------------ installed mods

    static File folder(Context c, Mod m) { return new File(game(c), m.folder); }
    static boolean installed(Context c, Mod m) { return folder(c, m).isDirectory(); }

    /** The names in the game folder, lower case: one look instead of one per catalog mod. */
    static Set<String> gameNames(Context c) {
        Set<String> out = new java.util.HashSet<>();
        String[] l = game(c).list();
        if (l != null) for (String s : l) out.add(key(s));
        return out;
    }

    /** False when the mod's folder can't be there; installed() for the rest. */
    static boolean maybeInstalled(Set<String> names, Mod m) {
        String k = key(m.folder);
        return k.startsWith(".") || k.startsWith("/") || names.contains(k.split("/", 2)[0]);
    }

    /** The version installed by this app (files/mods/<folder>.version), or "" when unknown. */
    static String installedVersion(Context c, Mod m) {
        try { return new String(Files.readAllBytes(new File(dir(c), safe(m.folder) + ".version").toPath()), StandardCharsets.UTF_8).trim(); }
        catch (IOException e) { return ""; }
    }

    static void setInstalledVersion(Context c, Mod m) throws IOException {
        dir(c).mkdirs();
        Files.write(new File(dir(c), safe(m.folder) + ".version").toPath(), m.version.getBytes(StandardCharsets.UTF_8));
    }

    static boolean updatable(Context c, Mod m) {
        if (!installed(c, m)) return false;
        String v = installedVersion(c, m);
        return !v.isEmpty() && Updater.compare(m.version, v) > 0;
    }

    private static String safe(String s) { return s.replaceAll("[^A-Za-z0-9._-]", "_"); }

    // ------------------------------------------------------------------ Memoria.ini: mods and their options
    //
    // As Memoria.Launcher's mod manager (MainWindow_ModManager.cs, Mod.cs) keeps them:
    //  FolderNames    the folders Memoria loads, highest priority first: each mod that is on, with
    //                 its chosen options' folders ("Mod/Options/X") around it by their Priority;
    //  Priorities     the installed mods' folders (a mod not listed is new: it gets its defaults);
    //  ActiveSubMods  every installed mod's chosen options ("Mod/Options/X"), on or off.

    /** The mods that are on (FolderNames without the options' folders), highest priority first. */
    static List<String> enabled(Context c) {
        List<String> out = new ArrayList<>();
        for (String f : list(iniLines(c), "FolderNames")) if (!f.contains("/")) out.add(f);
        return out;
    }

    /** Everything Memoria loads, options' folders included, highest priority first. */
    static List<String> folderNames(Context c) { return list(iniLines(c), "FolderNames"); }

    static boolean isEnabled(Context c, Mod m) {
        for (String f : enabled(c)) if (f.equalsIgnoreCase(m.folder)) return true;
        return false;
    }

    /** Turns a mod on or off; the list is kept in catalog priority order (highest first). */
    static void setEnabled(Context c, Mod m, boolean on) throws IOException { setEnabled(c, m.folder, on); }

    /** Turns a folder on or off. The other mods keep the player's order; one turned on goes where
     *  its catalog priority puts it (above the first mod with a lower one). */
    static void setEnabled(Context c, String folder, boolean on) throws IOException {
        Map<String, Set<String>> ch = choices(c);
        List<String> folders = enabled(c);
        folders.removeIf(f -> f.equalsIgnoreCase(folder));
        if (on) {
            List<Mod> all = mods(c);
            int p = priorityOf(all, folder), at = folders.size();
            for (int i = 0; i < folders.size(); i++) if (priorityOf(all, folders.get(i)) < p) { at = i; break; }
            folders.add(at, folder);
        }
        write(c, folders, ch);
    }

    /** Moves an enabled folder up (-1, higher priority) or down (+1). */
    static void move(Context c, String folder, int dir) throws IOException {
        Map<String, Set<String>> ch = choices(c);
        List<String> folders = enabled(c);
        int i = -1;
        for (int k = 0; k < folders.size(); k++) if (folders.get(k).equalsIgnoreCase(folder)) i = k;
        int j = i + dir;
        if (i < 0 || j < 0 || j >= folders.size()) return;
        Collections.swap(folders, i, j);
        write(c, folders, ch);
    }

    /** All installed mods on, in catalog priority order, or all off. */
    static void setAll(Context c, boolean on) throws IOException {
        Map<String, Set<String>> ch = choices(c);
        List<String> folders = new ArrayList<>();
        if (on) {
            List<Mod> all = mods(c);
            folders.addAll(installedFolders(c));
            Collections.sort(folders, (a, b) -> Integer.compare(priorityOf(all, b), priorityOf(all, a)));
        }
        write(c, folders, ch);
    }

    /** Writes Memoria.ini again from the mods that are on and the chosen options: before the game
     *  starts, so a mod installed or imported without its options gets Memoria's defaults. */
    static void sync(Context c) throws IOException {
        if (!new File(game(c), "Memoria.ini").isFile()) return;
        write(c, enabled(c), choices(c));
    }

    // ---- options

    /** An installed mod's options: from its ModDescription.xml (what Memoria's launcher reads),
     *  else from the catalog. */
    static List<SubMod> subMods(Context c, String folder) {
        Mod d = description(c, folder);
        if (d != null && !d.subMods.isEmpty()) return d.subMods;
        Mod m = byFolder(c, folder);
        return m != null ? m.subMods : new ArrayList<>();
    }

    /** How many options the player can choose for an installed mod. */
    static int choiceCount(Context c, String folder) {
        int n = 0;
        for (SubMod s : subMods(c, folder)) if (s.choice()) n++;
        return n;
    }

    /** The chosen options of an installed mod (lower-case paths inside the mod). */
    static Set<String> chosen(Context c, String folder) {
        Set<String> set = choices(c).get(key(folder));
        return set != null ? set : new java.util.HashSet<>();
    }

    static boolean isChosen(Set<String> chosen, SubMod s) { return chosen.contains(key(s.path)); }

    /** Chooses an option or drops it. As Memoria: choosing one of a group drops the others; dropping
     *  one brings back the group's default, if it has one. */
    static void choose(Context c, String folder, SubMod s, boolean on) throws IOException {
        Map<String, Set<String>> ch = choices(c);
        Set<String> set = ch.get(key(folder));
        if (set == null) { set = new java.util.HashSet<>(); ch.put(key(folder), set); }
        List<SubMod> subs = subMods(c, folder);
        if (on) {
            set.add(key(s.path));
            if (s.group != null) for (SubMod o : subs) if (o.choice() && s.group.equals(o.group) && !key(o.path).equals(key(s.path))) set.remove(key(o.path));
        } else {
            set.remove(key(s.path));
            if (s.group != null) for (SubMod o : subs) if (o.choice() && o.isDefault && s.group.equals(o.group)) { set.add(key(o.path)); break; }
        }
        write(c, enabled(c), ch);
    }

    /** The chosen options of every installed mod (lower-case paths, by lower-case mod folder). A mod
     *  Memoria.ini's Priorities doesn't list yet is new: it gets the options marked Default. */
    private static Map<String, Set<String>> choices(Context c) {
        List<String> lines = iniLines(c);
        List<String> known = list(lines, "Priorities");
        Set<String> saved = new java.util.HashSet<>();
        for (String x : list(lines, "ActiveSubMods")) saved.add(key(x));
        // a Memoria.ini from before ActiveSubMods: the options' folders in FolderNames are the chosen ones
        if (!has(lines, "ActiveSubMods")) for (String x : list(lines, "FolderNames")) if (x.contains("/")) saved.add(key(x));
        Map<String, Set<String>> out = new java.util.HashMap<>();
        for (String folder : installedFolders(c)) {
            boolean seen = containsIgnoreCase(known, folder);
            Set<String> set = new java.util.HashSet<>();
            for (SubMod s : subMods(c, folder))
                if (s.choice() && (seen ? saved.contains(key(folder + "/" + s.path)) : s.isDefault)) set.add(key(s.path));
            out.put(key(folder), set);
        }
        return out;
    }

    private static String key(String s) { return s.replace('\\', '/').toLowerCase(java.util.Locale.ROOT); }

    /** A mod copied into the game folder by hand (GitHub issue 15): what its own ModDescription.xml
     *  says (name, author, description, options), else just its folder's name. The folder is the
     *  one it is in, whatever the file calls it; it has no download. */
    static Mod handMod(Context c, String folder) {
        Mod m = description(c, folder);
        if (m == null) { m = new Mod(); m.name = folder; }
        if (m.name.isEmpty()) m.name = folder;
        m.folder = folder;
        m.url = "";
        if (!m.preview.startsWith("http")) m.preview = "";
        if (!m.website.startsWith("http")) m.website = "";
        return m;
    }

    /** The mod's own ModDescription.xml, or null. */
    private static Mod description(Context c, String folder) {
        File f = new File(new File(game(c), folder), "ModDescription.xml");
        long size = f.length(), time = f.lastModified();     // 0, 0: no file
        if (size == 0) return null;
        String k = f.getPath() + ":" + size + ":" + time;
        synchronized (ModCatalog.class) { if (descriptions.containsKey(k)) return descriptions.get(k); }
        Mod m = null;
        try {
            List<Mod> l = parse(f, false);
            if (!l.isEmpty()) m = l.get(0);
        } catch (IOException e) { return null; }
        synchronized (ModCatalog.class) { descriptions.put(k, m); }
        return m;
    }

    /** Parsed ModDescription.xml files, by path, size and time (null: no mod in it). */
    private static final Map<String, Mod> descriptions = new java.util.HashMap<>();

    // ---- writing

    private static void write(Context c, List<String> on, Map<String, Set<String>> ch) throws IOException {
        File game = game(c);
        // the mods that are on and exist, then the other installed ones (Priorities lists them all)
        List<String> order = new ArrayList<>();
        for (String f : on) if (new File(game, f).isDirectory() && !containsIgnoreCase(order, f)) order.add(f);
        int onCount = order.size();
        for (String f : installedFolders(c)) if (!containsIgnoreCase(order, f)) order.add(f);
        Set<String> onNames = new java.util.HashSet<>();
        for (int i = 0; i < onCount; i++) onNames.add(modName(c, order.get(i)));

        List<String> names = new ArrayList<>(), prios = new ArrayList<>(), active = new ArrayList<>();
        for (int i = 0; i < order.size(); i++) {
            String folder = order.get(i);
            prios.add(folder);
            List<SubMod> subs = subMods(c, folder);
            Set<String> chosen = ch.get(key(folder));
            Set<String> act = new java.util.HashSet<>(chosen != null ? chosen : Collections.<String>emptySet());
            // a group with nothing chosen gets its default (Memoria's UpdateModSettings)
            for (SubMod s : subs) {
                if (!s.choice() || s.group == null) continue;
                boolean any = false;
                SubMod def = null;
                for (SubMod o : subs) {
                    if (!o.choice() || !s.group.equals(o.group)) continue;
                    if (act.contains(key(o.path))) { any = true; break; }
                    if (o.isDefault) def = o;
                }
                if (!any && def != null) act.add(key(def.path));
            }
            for (SubMod s : subs) if (s.choice() && act.contains(key(s.path))) active.add(folder + "/" + s.path);
            // options that follow other mods: on when all of ActivateWithMod are on, or none of ActivateWithoutMod
            for (SubMod s : subs) {
                if (!s.auto()) continue;
                int found = 0;
                for (String n : s.withMod != null ? s.withMod : s.withoutMod) if (onNames.contains(n)) found++;
                boolean a = s.withMod != null ? found == s.withMod.size() : found == 0;
                if (a) act.add(key(s.path)); else act.remove(key(s.path));
            }
            if (i >= onCount) continue;
            // the mod's folder among its options' folders, by Priority (Mod.EnumerateModAndSubModsOrdered)
            List<SubMod> sorted = new ArrayList<>();
            for (SubMod s : subs) if (!s.header) sorted.add(s);
            Collections.sort(sorted, (a, b) -> Integer.compare(a.priority, b.priority));
            int k = 0;
            for (; k < sorted.size() && sorted.get(k).priority >= 0; k++) addSub(names, game, folder, sorted.get(k), act);
            names.add(folder);
            for (; k < sorted.size(); k++) addSub(names, game, folder, sorted.get(k), act);
        }
        List<String> lines = iniLines(c);
        Settings.put(lines, "Mod", "FolderNames", quoted(names));
        Settings.put(lines, "Mod", "Priorities", quoted(prios));
        Settings.put(lines, "Mod", "ActiveSubMods", quoted(active));
        Settings.writeIni(new File(game, "Memoria.ini"), lines);
    }

    private static void addSub(List<String> names, File game, String folder, SubMod s, Set<String> act) {
        String p = folder + "/" + s.path;
        if (act.contains(key(s.path)) && new File(game, p).isDirectory()) names.add(p);
    }

    /** A mod's name as other mods' ActivateWithMod give it: from its ModDescription.xml or the catalog. */
    private static String modName(Context c, String folder) {
        Mod d = description(c, folder);
        if (d != null && !d.name.isEmpty()) return d.name;
        Mod m = byFolder(c, folder);
        return m != null ? m.name : folder;
    }

    private static String quoted(List<String> l) {
        if (l.isEmpty()) return "";
        StringBuilder b = new StringBuilder();
        for (String x : l) b.append(b.length() > 0 ? ", " : "").append('"').append(x).append('"');
        return b.toString();
    }

    private static int priorityOf(List<Mod> all, String folder) {
        for (Mod m : all) if (m.folder.equalsIgnoreCase(folder)) return m.priority;
        return 0;
    }

    /** A Memoria.ini list ("a", "b" or a, b); empty when the key is missing or blank. */
    private static List<String> list(List<String> lines, String key) {
        List<String> out = new ArrayList<>();
        String v = value(lines, key);
        if (v == null || v.trim().isEmpty()) return out;
        if (v.contains("\"")) {
            java.util.regex.Matcher mt = java.util.regex.Pattern.compile("\"([^\"]*)\"").matcher(v);
            while (mt.find()) if (!mt.group(1).trim().isEmpty()) out.add(mt.group(1).trim());
        } else {
            for (String x : v.split(",")) if (!x.trim().isEmpty()) out.add(x.trim());
        }
        return out;
    }

    private static boolean has(List<String> lines, String key) { return value(lines, key) != null; }

    private static String value(List<String> lines, String key) {
        for (String line : lines) {
            String t = line.trim();
            if (t.startsWith(";") || t.startsWith("#")) continue;
            int eq = t.indexOf('=');
            if (eq > 0 && t.substring(0, eq).trim().equalsIgnoreCase(key)) return t.substring(eq + 1);
        }
        return null;
    }

    private static List<String> iniLines(Context c) {
        File ini = new File(game(c), "Memoria.ini");
        try { return new ArrayList<>(Files.readAllLines(ini.toPath(), StandardCharsets.ISO_8859_1)); }
        catch (IOException e) { return new ArrayList<>(); }
    }
}
