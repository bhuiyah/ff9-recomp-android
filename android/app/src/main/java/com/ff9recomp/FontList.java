package com.ff9recomp;

import java.io.File;
import java.io.IOException;
import java.io.RandomAccessFile;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/**
 * Fonts the game can use for Memoria's [Font] setting (GitHub issue 36): Android's own fonts and
 * the player's in files/fonts. On the PC Memoria offers the fonts installed in Windows; the engine
 * (src/engine/Text/Fonts.cs) looks in the same folders and calls each font by its file name
 * without the extension. Listed here: .ttf and .otf files with outlines (not the colour emoji) and
 * Latin letters, as the game's text needs them.
 */
final class FontList {
    static final String[] SYSTEM_DIRS = { "/system/fonts", "/product/fonts" };

    private static List<String> cached;

    /** Name -> file, sorted by name; the player's own fonts first. */
    static synchronized Map<String, File> fonts(File userDir) {
        Map<String, File> out = new LinkedHashMap<>();
        List<File> dirs = new ArrayList<>();
        if (userDir != null) dirs.add(userDir);
        for (String d : SYSTEM_DIRS) dirs.add(new File(d));
        for (File dir : dirs) {
            File[] fs = dir.listFiles();
            if (fs == null) continue;
            Arrays.sort(fs, (a, b) -> a.getName().compareToIgnoreCase(b.getName()));
            for (File f : fs) {
                String n = f.getName(), l = n.toLowerCase(Locale.ROOT);
                if (!f.isFile() || !(l.endsWith(".ttf") || l.endsWith(".otf"))) continue;
                String name = n.substring(0, n.length() - 4);
                if (!out.containsKey(name) && usable(f)) out.put(name, f);
            }
        }
        return out;
    }

    /** The names alone, worked out again only when the player's folder changes (reading ~300
     *  system fonts takes a moment). */
    static synchronized List<String> names(File userDir) {
        long s = stamp(userDir);
        if (cached == null || s != userStamp) {
            cached = new ArrayList<>(fonts(userDir).keySet());
            userStamp = s;
        }
        return cached;
    }

    private static long userStamp;
    private static long stamp(File d) {
        File[] fs = d != null ? d.listFiles() : null;
        long s = fs == null ? -1 : fs.length;
        if (fs != null) for (File f : fs) s = s * 31 + f.getName().hashCode() + f.lastModified() + f.length();
        return s;
    }

    /** An outline font (glyf or CFF) whose cmap has 'A' and 'a'. */
    static boolean usable(File f) {
        try (RandomAccessFile r = new RandomAccessFile(f, "r")) {
            int tag = r.readInt();
            if (tag != 0x00010000 && tag != 0x4F54544F /* OTTO */ && tag != 0x74727565 /* true */) return false;
            int n = r.readUnsignedShort();
            r.skipBytes(6);
            long cmap = -1;
            boolean outlines = false;
            for (int i = 0; i < n; i++) {
                int t = r.readInt();
                r.readInt();
                long off = r.readInt() & 0xFFFFFFFFL;
                r.readInt();
                if (t == 0x636D6170) cmap = off;                                    // cmap
                else if (t == 0x676C7966 || t == 0x43464620 || t == 0x43464632) outlines = true;   // glyf, CFF, CFF2
            }
            if (cmap < 0 || !outlines) return false;
            return glyph(r, cmap, 'A') != 0 && glyph(r, cmap, 'a') != 0;
        } catch (IOException | RuntimeException e) {
            return false;
        }
    }

    /** The glyph of ch from a Unicode subtable (format 4 or 12), or 0. */
    private static int glyph(RandomAccessFile r, long cmap, int ch) throws IOException {
        r.seek(cmap + 2);
        int n = r.readUnsignedShort();
        for (int i = 0; i < n; i++) {
            r.seek(cmap + 4 + i * 8L);
            int platform = r.readUnsignedShort(), enc = r.readUnsignedShort();
            long off = r.readInt() & 0xFFFFFFFFL;
            boolean unicode = platform == 0 || (platform == 3 && (enc == 1 || enc == 10));
            if (!unicode) continue;
            long st = cmap + off;
            r.seek(st);
            int format = r.readUnsignedShort();
            if (format == 4) {
                r.skipBytes(4);
                int segs = r.readUnsignedShort() / 2;
                long ends = st + 14, starts = ends + segs * 2L + 2, deltas = starts + segs * 2L, ranges = deltas + segs * 2L;
                for (int s = 0; s < segs; s++) {
                    r.seek(ends + s * 2L);
                    int end = r.readUnsignedShort();
                    if (end < ch) continue;
                    r.seek(starts + s * 2L);
                    int start = r.readUnsignedShort();
                    if (start > ch) break;
                    r.seek(deltas + s * 2L);
                    int delta = r.readShort();
                    r.seek(ranges + s * 2L);
                    int ro = r.readUnsignedShort();
                    if (ro == 0) return (ch + delta) & 0xFFFF;
                    r.seek(ranges + s * 2L + ro + (ch - start) * 2L);
                    int g = r.readUnsignedShort();
                    return g == 0 ? 0 : (g + delta) & 0xFFFF;
                }
            } else if (format == 12) {
                r.skipBytes(10);
                long groups = r.readInt() & 0xFFFFFFFFL;
                for (long g = 0; g < groups; g++) {
                    long a = r.readInt() & 0xFFFFFFFFL, b = r.readInt() & 0xFFFFFFFFL, gl = r.readInt() & 0xFFFFFFFFL;
                    if (ch >= a && ch <= b) return (int) (gl + ch - a);
                }
            }
        }
        return 0;
    }
}
