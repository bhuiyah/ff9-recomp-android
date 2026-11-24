package com.ff9recomp;

import android.content.SharedPreferences;

/**
 * Settings > Buttons: which button of the controller works as which (as FF8Recomp has it). Kept as
 * a permutation, so no button of the game's can be lost. Only the controller: the on-screen buttons
 * and the keyboard keep their own names. Hotkeys (GameActivity.hotkey) are taken before this.
 */
final class ButtonMap {
    static final String[] NAMES = { "A", "B", "X", "Y", "L1", "R1", "L2", "R2", "Select", "Start", "L3", "R3" };
    private static final int L2 = 6, R2 = 7;
    /** GameActivity's XInput bits; 0: a trigger. */
    private static final int[] BITS = { GameActivity.A, GameActivity.B, GameActivity.X, GameActivity.Y,
            GameActivity.LB, GameActivity.RB, 0, 0, GameActivity.BACK, GameActivity.START, GameActivity.LTHUMB, GameActivity.RTHUMB };
    private static final int MAPPED;
    static {
        int m = 0;
        for (int b : BITS) m |= b;
        MAPPED = m;
    }

    static int[] read(SharedPreferences prefs) { return parse(prefs.getString("button_map", "")); }

    /** "1,0,2,..." as the map, or every button as itself when it is not a permutation. */
    static int[] parse(String text) {
        int[] usual = new int[NAMES.length], m = new int[NAMES.length];
        for (int i = 0; i < usual.length; i++) usual[i] = i;
        String[] s = text.split(",");
        if (s.length != m.length) return usual;
        int seen = 0;
        try {
            for (int i = 0; i < m.length; i++) {
                m[i] = Integer.parseInt(s[i].trim());
                if (m[i] < 0 || m[i] >= m.length) return usual;
                seen |= 1 << m[i];
            }
        } catch (NumberFormatException e) { return usual; }
        return seen == (1 << m.length) - 1 ? m : usual;
    }

    /** The button works as another; the one that did takes over what it was. */
    static void choose(SharedPreferences prefs, int button, int as) {
        int[] m = read(prefs);
        for (int i = 0; i < m.length; i++) if (m[i] == as) m[i] = m[button];
        m[button] = as;
        StringBuilder s = new StringBuilder();
        for (int v : m) s.append(s.length() > 0 ? "," : "").append(v);
        prefs.edit().putString("button_map", s.toString()).apply();
    }

    static void reset(SharedPreferences prefs) { prefs.edit().remove("button_map").apply(); }

    /** The controller's buttons and triggers as the game gets them: { buttons, lt, rt }. */
    static int[] apply(int[] map, int buttons, int lt, int rt) {
        int[] out = { buttons & ~MAPPED, 0, 0 };
        for (int i = 0; i < map.length; i++) {
            int v = i == L2 ? lt : i == R2 ? rt : (buttons & BITS[i]) != 0 ? 255 : 0;
            if (v == 0) continue;
            if (map[i] == L2) out[1] = Math.max(out[1], v);
            else if (map[i] == R2) out[2] = Math.max(out[2], v);
            else if (v > 60) out[0] |= BITS[map[i]];
        }
        return out;
    }
}
