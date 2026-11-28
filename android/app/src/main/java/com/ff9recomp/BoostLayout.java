package com.ff9recomp;

/**
 * Where the touch controls' four boosters go (GitHub #64), kept free of Android types so it can be tested on
 * a PC. A row at the top between the shoulders when the four fit there with their names readable; else a row
 * lower down, between SELECT and START; else two by two there; else two by two as wide as there is room. On a
 * near-square screen (Galaxy Z Fold's inner screen) the top gap is far too narrow and the names overlapped.
 */
final class BoostLayout {
    private BoostLayout() { }

    /**
     * @param w screen width; unit the controls' unit; nameW the widest booster name as drawn
     * @param top y of the top row; low y of the lower row
     * @param shown the shown buttons as {left, top, right, bottom} each
     * @return the four boxes as {left, top, right, bottom} each
     */
    static float[][] place(float w, float unit, float nameW, float top, float low, float[][] shown) {
        float bh = unit * 0.75f, gap = unit * 0.15f, full = unit * 1.9f, minW = nameW + unit * 0.3f;
        float[][] out;
        if ((out = fit(w, unit, top, bh, gap, full, minW, 4, shown)) != null) return out;
        if ((out = fit(w, unit, low, bh, gap, full, minW, 4, shown)) != null) return out;
        if ((out = fit(w, unit, low, bh, gap, full, minW, 2, shown)) != null) return out;
        return fit(w, unit, low, bh, gap, full, 0, 2, shown);
    }

    /** The boxes in cols columns from y, in the free span around the middle; null when a box would be narrower than minW. */
    private static float[][] fit(float w, float unit, float y, float bh, float gap, float full, float minW, int cols,
                                 float[][] shown) {
        int rows = 4 / cols;
        float bottom = y + rows * bh + (rows - 1) * gap, lo = unit * 0.25f, hi = w - unit * 0.25f;
        for (float[] r : shown) {
            if (r[3] <= y || r[1] >= bottom) continue;
            if ((r[0] + r[2]) / 2 < w / 2) lo = Math.max(lo, r[2] + gap);
            else hi = Math.min(hi, r[0] - gap);
        }
        float bw = Math.min(full, (hi - lo - (cols - 1) * gap) / cols);
        if (bw < minW) {
            if (minW > 0) return null;
            bw = Math.max(bw, unit * 0.8f);
        }
        float total = cols * bw + (cols - 1) * gap;
        float rx = Math.max(lo, Math.min(hi - total, (w - total) / 2));
        float[][] out = new float[4][];
        for (int i = 0; i < 4; i++) {
            float bx = rx + (i % cols) * (bw + gap), by = y + (i / cols) * (bh + gap);
            out[i] = new float[] { bx, by, bx + bw, by + bh };
        }
        return out;
    }
}
