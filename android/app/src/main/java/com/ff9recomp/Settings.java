package com.ff9recomp;

import android.content.Context;
import android.content.SharedPreferences;

import java.io.File;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;

/**
 * The launcher's game settings. They live in the launcher's preferences (so a reset or replaced
 * Memoria.ini does not lose them) and are written into the imported game's Memoria.ini each time
 * the game starts; Memoria reads them from there. Only the keys below are touched: comments and
 * every other line of the file stay as they are.
 */
final class Settings {
    /** One setting: its preference key, the launcher's label and note, its choices, and what each writes. */
    static final class Option {
        final String key, label, note;
        final String[] choices;
        final int def;
        Option(String key, String label, String note, int def, String... choices) {
            this.key = key; this.label = label; this.note = note; this.def = def; this.choices = choices;
        }
        boolean isBool() { return choices.length == 0; }
    }

    static final Option[] OPTIONS = {
        new Option("fps", "Frame rate", "Original: fields 30, battles 15, as the game was made. Smooth: Memoria fills in frames.", 1,
                "Original", "Smooth 60", "Smooth 120"),
        new Option("start", "Start", "Continue: straight back to the place you were last (Memoria's autosave), skipping the title.", 0,
                "Title screen", "Continue"),
        new Option("resolution", "Resolution", "Lower draws fewer pixels: cooler and longer battery, softer picture. The screen scales it up for free.", 0,
                "Full", "75%", "50%", "25%"),
        new Option("widescreen", "Widescreen", "Fields and battles fill a 16:9 screen.", 1),
        // GitHub issues 4, 32 and 50: no black bars, by stretching (engine Gfx/Pipeline.cs, GameActivity)
        new Option("stretch", "Picture", "Black bars or a stretched picture: some areas are drawn 4:3, phones are wider than 16:9, and some handhelds are squarer than 4:3.", 0,
                "Keep its shape", "Stretch narrow areas", "Stretch everything to the screen"),
        // GitHub issues 2 and 8: the phone held upright (GameActivity.arrange)
        new Option("portrait", "Portrait (phones)", "Hold the phone upright: the game on top, the party panels and touch controls under it.", 0),
        // FF7Recomp's 1.1.35/1.1.36 (GameActivity.arrangeSide): one wide screen, the game and the panels beside each other
        new Option("side", "Side by side (wide screens)", "The game and the second screen's panels (party, map, items, Save/Load, speed) beside each other on one screen. Held upright it is like Portrait.", 0),
        new Option("side_left", "Side by side: panels on the left", "The panels at the game's left instead of its right. In the game, the button at the top of the panels hides them; the tab at the screen's edge brings them back.", 0),
        // as FF7Recomp's GitHub #87 (PanelButton)
        new Option("panel_button", "Button for the panels (one screen)", "A small button over the game, when it fills the screen, opens the second screen's panels (party, map, items, Save/Load, speed). "
                + "Their arrow, or a tap on the game beside them, closes them. Drag the button to move it. A controller button can do it too (Controls).", 0),
        new Option("skip_intros", "Skip logos", "Straight to the title screen.", 1),
        new Option("speed", "Speed-up booster", "How fast the game runs while the High Speed booster is on.", 1,
                "2x", "3x", "4x", "5x"),
        new Option("boosters", "Boosters", "The game's own boosters: High Speed, Battle Assistance, No Encounters and more (in-game menu).", 1),
        new Option("turbo_dialog", "Fast dialog", "Holding confirm speeds text along.", 1),
        new Option("swap_confirm", "Swap confirm and cancel", "B confirms and A cancels (Japanese layout).", 0),
        new Option("analog_run", "Analog walk and run", "Walk or run by how far the stick is pushed.", 1),
        new Option("bottom_screen", "Second screen panels", "On two-screen devices: party HP/MP, ATB, enemies and gil on the second screen. Off leaves it to other apps.", 1),
        new Option("bottom_buttons", "Second screen buttons", "The command deck under the panels, also where they are on the only screen: Menu, Save, Load, Speed, Battles, Battle+.", 1),
        new Option("panel_dim", "Dim the panels in cutscenes", "The second screen darkens while a scene plays and you have no control, and brightens when you have it again.", 1),
        new Option("swap_screens", "Swap screens", "The game on the second screen and the panels on the main one (for devices whose main screen is the small one).", 0),
        new Option("touch_controls", "Touch controls", "Auto: on screen only when no controller is connected.", 0,
                "Auto", "Always", "Never"),
        new Option("panel_key", "Open the panels with", "On one screen, this controller button opens and closes the panels over the game, or hides and shows Side by side's. The game doesn't get it then.", 0,
                "Off", "Start", "L3", "R3"),
        new Option("rumble", "Rumble", "The game's vibration on the controller, or on the device.", 1, "Off", "Light", "Strong"),
        new Option("crt_filter", "CRT filter", "Scanlines and a soft vignette over the game and the panels.", 0),
        new Option("show_fps", "Show FPS", "Frames per second in the top corner.", 0),
        new Option("menu_sounds", "Menu sounds", "The launcher's cursor sounds, FF9's own from your game files.", 1),
        new Option("music_volume", "Music volume", null, 4, "0%", "25%", "50%", "75%", "100%"),
        new Option("sound_volume", "Sound volume", null, 4, "0%", "25%", "50%", "75%", "100%"),
    };

    static final List<String> INTS = new ArrayList<>(), BOOLS = new ArrayList<>();
    static {
        for (Option o : OPTIONS) (o.isBool() ? BOOLS : INTS).add(o.key);
    }

    private Settings() {}

    static SharedPreferences prefs(Context c) { return c.getSharedPreferences("launcher", Context.MODE_PRIVATE); }

    /** Steam Cloud saves on Game files > Saves. Whether syncing is on, and the result of the last sync shown there. */
    static final String CLOUD_SAVES = "cloud_saves", CLOUD_LAST = "cloud_last";
    static boolean cloudSaves(Context c) { return prefs(c).getBoolean(CLOUD_SAVES, false); }
    /** Upload on the title screen's Cloud Data menu goes to Steam at once, while the game runs (GitHub issue 21). */
    static final String CLOUD_UPLOAD_NOW = "cloud_upload_now";
    static boolean cloudUploadNow(Context c) { return prefs(c).getBoolean(CLOUD_UPLOAD_NOW, true); }

    static int get(Context c, Option o) {
        SharedPreferences p = prefs(c);
        if (o.isBool()) return p.getBoolean(o.key, o.def != 0) ? 1 : 0;
        return Math.max(0, Math.min(o.choices.length - 1, p.getInt(o.key, o.def)));
    }

    static void set(Context c, Option o, int v) {
        if (o.isBool()) prefs(c).edit().putBoolean(o.key, v != 0).apply();
        else prefs(c).edit().putInt(o.key, v).apply();
    }

    static String valueText(Context c, Option o) {
        int v = get(c, o);
        return o.isBool() ? (v != 0 ? "On" : "Off") : o.choices[v];
    }

    static Option find(String key) {
        for (Option o : OPTIONS) if (o.key.equals(key)) return o;
        return null;
    }

    /** Writes the settings into game/Memoria.ini (before the game starts). */
    static void apply(Context c, File game) throws IOException {
        File ini = new File(game, "Memoria.ini");
        if (!ini.isFile()) return;
        List<String> lines = new ArrayList<>(Files.readAllLines(ini.toPath(), StandardCharsets.ISO_8859_1));
        int fps = get(c, find("fps"));
        // Original: the game's own rates (fields 30, battles 15, world 20); Smooth: Memoria draws
        // in-between frames at 60 or 120. Not -1 ("the display's rate"): 120 on the Thor, hot.
        put(lines, "Graphics", "BattleFPS", fps == 0 ? "15" : fps == 1 ? "60" : "120");
        put(lines, "Graphics", "FieldFPS", fps == 0 ? "30" : fps == 1 ? "60" : "120");
        put(lines, "Graphics", "WorldFPS", fps == 0 ? "20" : fps == 1 ? "60" : "120");
        put(lines, "Graphics", "WidescreenSupport", on(c, "widescreen"));
        put(lines, "Graphics", "SkipIntros", get(c, find("skip_intros")) != 0 ? "3" : "0");
        put(lines, "Cheats", "Enabled", on(c, "boosters"));
        put(lines, "Cheats", "SpeedFactor", String.valueOf(get(c, find("speed")) + 2));
        put(lines, "Control", "TurboDialog", on(c, "turbo_dialog"));
        put(lines, "Control", "SwapConfirmCancel", on(c, "swap_confirm"));
        put(lines, "AnalogControl", "Enabled", on(c, "analog_run"));
        put(lines, "Audio", "MusicVolume", String.valueOf(get(c, find("music_volume")) * 25));
        put(lines, "Audio", "SoundVolume", String.valueOf(get(c, find("sound_volume")) * 25));
        // SoLoud, the only sound system on Android. A Memoria.ini without a Backend line (GitHub
        // issue 20) left Memoria on the PC's SdLib: SoundLib.Awake failed, and the title screen's
        // setup with it, so its menu never took input.
        put(lines, "Audio", "Backend", "2");
        tileSize(c, lines, game);
        writeIni(ini, lines);
    }

    /**
     * Moguri's HD field backgrounds are cut in 64-pixel tiles, the game's own in 32 ([Graphics]
     * TileSize). Moguri 9 (MoguriMain) says so in its own Memoria.ini, which Memoria reads over the
     * main one; the older installer's MoguriFiles doesn't, and with TileSize = 32 every field
     * background came out as scrambled and black squares (GitHub issue 28). So: an active Moguri
     * backgrounds folder that doesn't set it gets 64, and the game's 32 comes back once it is off.
     */
    static void tileSize(Context c, List<String> lines, File game) {
        boolean moguri = false;
        for (String f : activeFolders(lines)) {
            if (setsTileSize(new File(game, f))) return;     // that mod decides
            String n = f.toLowerCase(java.util.Locale.ROOT);
            if (n.startsWith("moguri") && !n.contains("video") && !n.contains("soundtrack") && !n.contains("music")) { moguri = true; break; }
        }
        SharedPreferences p = prefs(c);
        if (moguri) {
            put(lines, "Graphics", "TileSize", "64");
            p.edit().putBoolean("tile_size_64", true).apply();
        } else if (p.getBoolean("tile_size_64", false)) {
            put(lines, "Graphics", "TileSize", "32");
            p.edit().remove("tile_size_64").apply();
        }
    }

    /** [Mod] FolderNames: the active mod folders, highest priority first. */
    static List<String> activeFolders(List<String> lines) {
        List<String> r = new ArrayList<>();
        java.util.regex.Pattern key = java.util.regex.Pattern.compile("^\\s*FolderNames\\s*=(.*)$");
        java.util.regex.Pattern quoted = java.util.regex.Pattern.compile("\"([^\"]*)\"");
        for (String l : lines) {
            java.util.regex.Matcher m = key.matcher(l);
            if (!m.matches()) continue;
            java.util.regex.Matcher q = quoted.matcher(m.group(1));
            while (q.find()) if (!q.group(1).trim().isEmpty()) r.add(q.group(1).trim());
            break;
        }
        return r;
    }

    private static boolean setsTileSize(File modFolder) {
        File ini = new File(modFolder, "Memoria.ini");
        if (!ini.isFile()) return false;
        try {
            for (String l : Files.readAllLines(ini.toPath(), StandardCharsets.ISO_8859_1))
                if (l.trim().matches("(?i)TileSize\\s*=\\s*\\d+")) return true;
        } catch (IOException e) {
            return false;
        }
        return false;
    }

    static void writeIni(File ini, List<String> lines) throws IOException {
        writeIni(ini, (String.join("\n", lines) + "\n").getBytes(StandardCharsets.ISO_8859_1));
    }

    /** Writes Memoria.ini. One saved by a file manager or a PC (GitHub issue 9) can belong to
     *  another app and be read-only to this one: then a new file takes its place (the folder is
     *  this app's, so it can remove the old one). */
    static void writeIni(File ini, byte[] b) throws IOException {
        try {
            Files.write(ini.toPath(), b);
            return;
        } catch (IOException e) {
            File tmp = new File(ini.getPath() + ".new");
            Files.write(tmp.toPath(), b);
            if (ini.exists() && !ini.delete()) { tmp.delete(); throw new IOException("cannot replace " + ini.getName() + ": " + e.getMessage()); }
            if (!tmp.renameTo(ini)) throw new IOException("cannot write " + ini.getName());
            android.util.Log.i("FF9Recomp", "replaced " + ini.getName() + " (" + e + ")");
        }
    }

    private static String on(Context c, String key) { return get(c, find(key)) != 0 ? "1" : "0"; }

    /** Sets "Key = value" in [section] (the first such line), adding it at the section's end if absent. */
    static void put(List<String> lines, String section, String key, String value) {
        int start = -1, end = lines.size();
        for (int i = 0; i < lines.size(); i++) {
            String t = lines.get(i).trim();
            if (t.startsWith("[") && t.endsWith("]")) {
                if (start >= 0) { end = i; break; }
                if (t.substring(1, t.length() - 1).trim().equalsIgnoreCase(section)) start = i;
            }
        }
        if (start < 0) {
            lines.add("");
            lines.add("[" + section + "]");
            lines.add(key + " = " + value);
            return;
        }
        for (int i = start + 1; i < end; i++) {
            String t = lines.get(i).trim();
            if (t.startsWith(";") || t.startsWith("#")) continue;
            int eq = t.indexOf('=');
            if (eq > 0 && t.substring(0, eq).trim().equalsIgnoreCase(key)) {
                lines.set(i, key + " = " + value);
                return;
            }
        }
        int at = end;
        while (at > start + 1 && lines.get(at - 1).trim().isEmpty()) at--;
        lines.add(at, key + " = " + value);
    }
}
