package com.ff9recomp;

import java.io.File;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

/**
 * Memoria's own settings as switches and choices (as Memoria.Launcher's Settings, Cheats and
 * Advanced tabs have them on the PC), read from and written into the game's Memoria.ini.
 * Not here: what the app's Settings write each time the game starts (frame rate, widescreen,
 * skip logos, boosters and speed, fast dialog, swap confirm, analog walk, volumes).
 *
 * A section with an "Enabled" key ([Battle], [Hacks], [Shaders]...) is ignored by Memoria while
 * it is 0, so changing one of its options turns it on (its other keys keep their values, which
 * are Memoria's defaults until changed).
 */
final class MemoriaSettings {

    /** One choice: its label and the keys it sets ("Section.Key=value"). */
    static final class Choice {
        final String label;
        final String[] sets;
        Choice(String label, String... sets) { this.label = label; this.sets = sets; }
    }

    static final class Opt {
        final String group, label, note;
        final Choice[] choices;     // two choices labelled Off / On: shown as a switch
        final boolean toggle;
        Opt(String group, String label, String note, boolean toggle, Choice... choices) {
            this.group = group; this.label = label; this.note = note; this.toggle = toggle; this.choices = choices;
        }
    }

    private static Opt toggle(String group, String label, String note, String key, String off, String on) {
        return new Opt(group, label, note, true, new Choice("Off", key + "=" + off), new Choice("On", key + "=" + on));
    }

    private static Opt pick(String group, String label, String note, Choice... choices) {
        return new Opt(group, label, note, false, choices);
    }

    private static Choice c(String label, String... sets) { return new Choice(label, sets); }

    /** A choice per value of one key. */
    private static Choice[] values(String key, String[] labels, String[] values) {
        Choice[] out = new Choice[labels.length];
        for (int i = 0; i < labels.length; i++) out[i] = new Choice(labels[i], key + "=" + values[i]);
        return out;
    }

    private static Choice[] shaders(String where) {
        String r = "Shaders.Shader_" + where + "_Realism", t = "Shaders.Shader_" + where + "_Toon", o = "Shaders.Shader_" + where + "_Outlines";
        return new Choice[] {
            c("Original", r + "=0", t + "=0", o + "=0"),
            c("Realism", r + "=1", t + "=0", o + "=0"),
            c("Toon", r + "=0", t + "=1", o + "=0"),
            c("Realism + outlines", r + "=1", t + "=0", o + "=1"),
            c("Toon + outlines", r + "=0", t + "=1", o + "=1"),
        };
    }

    static final String GRAPHICS = "Graphics", INTERFACE = "Interface", BATTLE = "Battle", CARDS = "Card game",
            WORLD = "World map", SAVES = "Saves", CHEATS = "Memoria cheats";

    static final Opt[] OPTIONS = {
        pick(GRAPHICS, "Field shader", "How characters are lit and drawn on fields.", shaders("Field")),
        pick(GRAPHICS, "Battle shader", "How characters and monsters are lit and drawn in battles.", shaders("Battle")),
        pick(GRAPHICS, "Stable camera", "How much the field camera lags behind the player: smoother, or more like the original.",
                values("Graphics.CameraStabilizer", new String[] { "Off", "Light", "Default", "Strong" }, new String[] { "0", "50", "85", "95" })),
        pick(GRAPHICS, "Background filtering", "Field backgrounds: smooth, or square pixels as on the PlayStation.",
                values("Graphics.FieldSmoothTexture", new String[] { "Square pixels", "Smooth", "Smoother" }, new String[] { "0", "1", "2" })),
        pick(GRAPHICS, "Garnet's hair", "Her hair after she cuts it, for the rest of the game.",
                values("Graphics.GarnetHair", new String[] { "As the story goes", "Always long", "Always short" }, new String[] { "0", "1", "2" })),
        toggle(GRAPHICS, "Skip the battle swirl", "Battles start at once, without the swirl.", "Graphics.BattleSwirlFrames", "115", "0"),

        pick(INTERFACE, "Battle menu", "The commands as a 2x3 table (Steam), or in one column with Change and Defend on left and right (PlayStation).",
                values("Interface.PSXBattleMenu", new String[] { "Steam", "PlayStation" }, new String[] { "0", "1" })),
        toggle(INTERFACE, "Scan panel", "Scan opens a panel with everything at once, instead of one message after another.", "Interface.ScanDisplay", "0", "1"),
        toggle(INTERFACE, "Thicker ATB bars", "Taller ATB and Trance bars in battle.", "Interface.ThickerATBBar", "0", "1"),
        pick(INTERFACE, "Text appearing", "Each letter of a dialog fades in, or appears at once as in the original.",
                values("Interface.TextFadeDuration", new String[] { "At once", "Fade in" }, new String[] { "0", "100" })),
        pick(INTERFACE, "Menu transitions", "The fade between menu screens.",
                values("Interface.FadeDuration", new String[] { "Instant", "Quick", "Original" }, new String[] { "0", "40", "300" })),
        toggle(INTERFACE, "Disc change screens", "Show the PlayStation's \"insert disc\" screens.", "Interface.DisplayPSXDiscChanges", "0", "1"),
        toggle(INTERFACE, "Only the PlayStation's bubbles", "Hide the speech bubbles the Steam release added.", "Icons.HideSteam", "0", "1"),
        toggle(INTERFACE, "Card bubbles", "Show the card bubbles over players you can challenge.", "Icons.HideCards", "1", "0"),
        pick(INTERFACE, "World map minimap", "Which side of the screen.",
                values("Interface.MinimapPreset", new String[] { "Left", "Right" }, new String[] { "0", "1" })),

        pick(BATTLE, "ATB mode", "Normal: enemies act while you choose. Fast: the gauges never wait. Turn-based: everyone waits while you choose. Simultaneous: actions play at the same time.",
                values("Battle.Speed", new String[] { "Normal", "Fast", "Turn-based", "Simultaneous" }, new String[] { "0", "1", "2", "5" })),
        pick(BATTLE, "ATB speed", "How fast the ATB gauges fill.",
                values("Battle.ATBSpeedFactor", new String[] { "Slowest", "Slow", "Normal", "Default", "Fast", "Fastest" }, new String[] { "1.0", "2.0", "3.0", "4.0", "5.0", "6.0" })),
        toggle(BATTLE, "Trance on demand", "Trance doesn't start on its own.", "Battle.NoAutoTrance", "0", "1"),
        toggle(BATTLE, "Damage above 9999", "Damage isn't capped at 9999.", "Battle.BreakDamageLimit", "0", "1"),
        toggle(BATTLE, "Garnet can always concentrate", "No \"Garnet can't concentrate\" in battle.", "Battle.GarnetConcentrate", "0", "1"),
        pick(BATTLE, "Menus in battle", "Open the Equip and Support menus during a battle.",
                values("Battle.AccessMenus", new String[] { "No", "Takes a turn", "On a character's turn", "Any time" }, new String[] { "0", "1", "2", "3" })),
        toggle(BATTLE, "PlayStation encounter rate", "Random battles as often as on the PlayStation (the Steam release made them rarer).", "Battle.PSXEncounterMethod", "0", "1"),

        pick(CARDS, "Rules", "Tetra Master, Triple Triad (as in FF8), or a mix of both.",
                values("TetraMaster.TripleTriad", new String[] { "Tetra Master", "Tetra Triad", "Triple Triad" }, new String[] { "0", "1", "2" })),
        pick(CARDS, "Randomness", "How much luck decides a card battle.",
                values("TetraMaster.ReduceRandom", new String[] { "Original", "Less random", "Easy wins" }, new String[] { "0", "1", "2" })),
        pick(CARDS, "Card limit", "How many cards you can carry.",
                values("TetraMaster.MaxCardCount", new String[] { "100", "1,000", "10,000" }, new String[] { "100", "1000", "10000" })),
        toggle(CARDS, "Auto discard button", "An Auto button when you have to discard cards.", "TetraMaster.DiscardAutoButton", "0", "1"),

        pick(WORLD, "View distance", "How far you see before the mist and fog.",
                c("Original", "Worldmap.MistViewDistance=100", "Worldmap.NoMistViewDistance=100"),
                c("x2", "Worldmap.MistViewDistance=200", "Worldmap.NoMistViewDistance=200"),
                c("x3", "Worldmap.MistViewDistance=290", "Worldmap.NoMistViewDistance=300"),
                c("Farthest", "Worldmap.MistViewDistance=450", "Worldmap.NoMistViewDistance=450")),
        pick(WORLD, "Field of view", "How wide the world map camera sees.",
                values("Worldmap.FieldOfView", new String[] { "Default (44)", "55", "65", "75" }, new String[] { "44", "55", "65", "75" })),
        pick(WORLD, "Camera height", "How high the camera sits above you.",
                values("Worldmap.CameraHeight", new String[] { "Lower", "Default", "Higher", "Highest" }, new String[] { "80", "100", "130", "160" })),
        toggle(WORLD, "Camera boost", "On a ship or airship, the view widens with speed.", "Worldmap.FieldOfViewSpeedBoost", "0", "100"),
        toggle(WORLD, "Camera tilt", "The camera tilts as a ship turns.", "Worldmap.CameraTiltShip", "0", "100"),
        toggle(WORLD, "Alternative flight controls", "Left stick moves, right stick turns the camera and changes height.", "Worldmap.AlternateControls", "0", "1"),

        pick(SAVES, "Autosave", "Memoria's autosave (Continue on the title screen, and Play from here).",
                c("Every new area", "SaveFile.DisableAutoSave=0", "SaveFile.AutoSaveOnlyAtMoogle=0"),
                c("At save points only", "SaveFile.DisableAutoSave=0", "SaveFile.AutoSaveOnlyAtMoogle=1"),
                c("Off", "SaveFile.DisableAutoSave=1", "SaveFile.AutoSaveOnlyAtMoogle=0")),

        toggle(CHEATS, "Skip naming", "Characters keep their default names; no naming screens.", "Hacks.DisableNameChoice", "0", "1"),
        pick(CHEATS, "Stealing", "Steal always works, or always gets the rarest item.",
                values("Hacks.StealingAlwaysWorks", new String[] { "Original", "Always works", "Always the rarest" }, new String[] { "0", "1", "2" })),
        pick(CHEATS, "Sword fight with Blank", "The Prima Vista's swordplay mini-game.",
                values("Hacks.SwordplayAssistance", new String[] { "PlayStation", "Steam (+30%)", "No time limit" }, new String[] { "0", "1", "2" })),
        pick(CHEATS, "Rope jumping", "Points per jump.",
                values("Hacks.RopeJumpingIncrement", new String[] { "1", "10", "100" }, new String[] { "1", "10", "100" })),
        pick(CHEATS, "Frog catching", "Frogs per catch.",
                values("Hacks.FrogCatchingIncrement", new String[] { "1", "5", "10" }, new String[] { "1", "5", "10" })),
        toggle(CHEATS, "Excalibur II any time", "Get Excalibur II even after the time limit.", "Hacks.ExcaliburIINoTimeLimit", "0", "1"),
        toggle(CHEATS, "Attack 9999 booster", "Offered in the in-game booster menu.", "Cheats.Attack9999", "0", "1"),
        toggle(CHEATS, "Battle Assistance booster", "Offered in the in-game booster menu.", "Cheats.BattleAssistance", "0", "1"),
        toggle(CHEATS, "No Encounters booster", "Offered in the in-game booster menu.", "Cheats.NoRandomEncounter", "0", "1"),
        toggle(CHEATS, "Master Skill booster", "Offered in the in-game booster menu. Can't be undone in a save.", "Cheats.MasterSkill", "0", "1"),
        toggle(CHEATS, "Level 99 booster", "Offered in the in-game booster menu. Can't be undone in a save.", "Cheats.LvMax", "0", "1"),
        toggle(CHEATS, "Max gil booster", "Offered in the in-game booster menu. Can't be undone in a save.", "Cheats.GilMax", "0", "1"),
    };

    static final String[] GROUPS = { GRAPHICS, INTERFACE, BATTLE, CARDS, WORLD, SAVES, CHEATS };

    // ---- reading and writing

    static List<String> read(File ini) throws IOException {
        return new ArrayList<>(Files.readAllLines(ini.toPath(), StandardCharsets.ISO_8859_1));
    }

    /** The choice that matches the file, or -1 (set by hand to something else). */
    static int current(List<String> lines, Opt o) {
        for (int i = 0; i < o.choices.length; i++) {
            boolean all = true;
            for (String s : o.choices[i].sets) {
                int dot = s.indexOf('.'), eq = s.indexOf('=');
                String v = get(lines, s.substring(0, dot), s.substring(dot + 1, eq));
                if (v == null || !same(v, s.substring(eq + 1))) { all = false; break; }
            }
            if (all) return i;
        }
        return -1;
    }

    private static boolean same(String a, String b) {
        a = a.trim(); b = b.trim();
        if (a.equalsIgnoreCase(b)) return true;
        try { return Double.parseDouble(a) == Double.parseDouble(b); } catch (NumberFormatException e) { return false; }
    }

    /** Sets a choice's keys, and turns their sections on. */
    static void apply(List<String> lines, Choice ch) {
        for (String s : ch.sets) {
            int dot = s.indexOf('.'), eq = s.indexOf('=');
            String section = s.substring(0, dot);
            String key = s.substring(dot + 1, eq);
            Settings.put(lines, section, key, s.substring(eq + 1));
            if (!key.equalsIgnoreCase("Enabled") && get(lines, section, "Enabled") != null && !setsEnabled(ch, section))
                Settings.put(lines, section, "Enabled", "1");
        }
    }

    /** The choice sets its section's Enabled itself ("Font.Enabled=0" is the game's own font). */
    private static boolean setsEnabled(Choice ch, String section) {
        for (String s : ch.sets) if (s.regionMatches(true, 0, section + ".Enabled=", 0, section.length() + 9)) return true;
        return false;
    }

    static void write(File ini, List<String> lines) throws IOException { Settings.writeIni(ini, lines); }

    /** "Key = value" in [section], or null. */
    static String get(List<String> lines, String section, String key) {
        boolean in = false;
        for (String line : lines) {
            String t = line.trim();
            if (t.startsWith("[") && t.endsWith("]")) { in = t.substring(1, t.length() - 1).trim().equalsIgnoreCase(section); continue; }
            if (!in || t.startsWith(";") || t.startsWith("#")) continue;
            int eq = t.indexOf('=');
            if (eq > 0 && t.substring(0, eq).trim().equalsIgnoreCase(key)) return t.substring(eq + 1).trim();
        }
        return null;
    }

    static List<Opt> inGroup(String g) {
        List<Opt> out = new ArrayList<>();
        if (g.equals(INTERFACE)) out.add(fontOpt());
        for (Opt o : OPTIONS) if (o.group.equals(g)) out.add(o);
        return out;
    }

    // ---- the text font (GitHub issue 36)
    /** The player's own fonts (files/fonts), set by the launcher. */
    static File userFonts;
    static final String PSX_FONT = "\"Alexandria\", \"Garnet\"";

    /** Memoria launcher's Font list: the game's font, the PlayStation's (Alexandria and Garnet,
     *  in Memoria's own font bundle; its "Use PSX font" switch sets the same), then the fonts the
     *  engine finds on the device (FontList), with Memoria's fallback after each as on the PC. */
    static Opt fontOpt() {
        List<Choice> cs = new ArrayList<>();
        cs.add(c("The game's", "Font.Enabled=0"));
        cs.add(c("PlayStation", "Font.Enabled=1", "Font.Names=" + PSX_FONT));
        for (String n : FontList.names(userFonts)) cs.add(c(n, "Font.Enabled=1", "Font.Names=\"" + n + "\", \"Times Bold\""));
        return pick(INTERFACE, "Font", "The text font: the game's, the PlayStation's, or one of this device's fonts. "
                + "Your own .ttf or .otf files go in the fonts folder next to the game folder.", cs.toArray(new Choice[0]));
    }

    static boolean known(String g) { return Arrays.asList(GROUPS).contains(g); }
}
