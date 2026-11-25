package com.ff9recomp;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.RectF;
import android.graphics.Typeface;
import android.view.HapticFeedbackConstants;
import android.view.MotionEvent;
import android.view.View;

/**
 * An on-screen pad over the game, for devices with one screen and no controller (GameActivity
 * decides when it shows). It feeds the same XInput-style state as a real pad:
 *
 *   D-pad (8-way, left)          face buttons (right): A = confirm (the game's confirm),
 *   L1 / L2 (top left)            B = cancel, Y = menu, X = the fourth button
 *   R1 / R2 (top right)          SELECT / START (bottom middle)
 *
 * Multi-touch: every finger presses what is under it, and a finger can slide from one button to
 * another (the D-pad follows the finger's angle).
 *
 * Settings > On-screen controls (GitHub #28): a size, an opacity and where each of the four groups
 * sits (the D-pad, the face buttons, L2/L1/SELECT, R1/R2/START), as a layout string (layout()).
 * In edit mode (TouchLayoutActivity) a finger drags a group instead of pressing buttons.
 *
 * Optional (Settings > On-screen controls > Size and layout, "Right stick, L3 and R3", as
 * FF7Recomp's GitHub #62): an analog right stick above the face buttons (a fifth group; Memoria's
 * world map camera with [AnalogControl] RightStickCamera) and L3 / R3 under SELECT / START
 * (Memoria's [Control] L3BindCheat / R3BindCheat, e.g. high speed mode).
 *
 * BOOST (under START, GitHub #44): opens a row of the four boosters at the top of the screen
 * (High speed, No battles, Battle assist, 9999 damage), the ones the AYN Thor's bottom screen has
 * on its Extras page; a tap turns one on or off (GameActivity.commandFromPanel, engine
 * Core/Companion.cs), and BOOST again closes the row. Not sent to the game as a button.
 *
 * Optional (Settings > On-screen controls > Size and layout, "Left stick instead of the D-pad",
 * GitHub #44): the D-pad's ring becomes an analog left stick (LX/LY), so a light push walks and a
 * full one runs.
 *
 * Any button can be hidden on its own (Settings > On-screen controls > Buttons shown, as FF7Recomp's
 * GitHub #88): a hidden one is neither drawn nor pressed, and the others stay where they are.
 */
final class TouchControls extends View {
    interface Listener { void pad(int buttons, int lt, int rt, int lx, int ly, int rx, int ry); }

    private static final int UP = 0x0001, DOWN = 0x0002, LEFT = 0x0004, RIGHT = 0x0008;
    private static final int START = 0x0010, SELECT = 0x0020, L1 = 0x0100, R1 = 0x0200;
    private static final int CIRCLE = 0x2000, CROSS = 0x1000, SQUARE = 0x4000, TRIANGLE = 0x8000;
    private static final int L3 = 0x0040, R3 = 0x0080;
    private static final int L2 = 1 << 20, R2 = 1 << 21;    // triggers: sent as analog values
    private static final int BOOST = 1 << 22;                 // the boosters' row (not a pad button)

    private static final class Btn {
        final int bit; final String label, hint; final boolean round;
        final RectF r = new RectF();
        boolean hidden;   // Buttons shown
        Btn(int bit, String label, String hint, boolean round) { this.bit = bit; this.label = label; this.hint = hint; this.round = round; }
    }

    // face buttons laid out as an Android pad: A bottom, B right, X left, Y top
    private final Btn a = new Btn(CIRCLE, "A", "OK", true), b = new Btn(CROSS, "B", "Back", true),
            x = new Btn(SQUARE, "X", "", true), y = new Btn(TRIANGLE, "Y", "Menu", true),
            l1 = new Btn(L1, "L1", "", false), l2 = new Btn(L2, "L2", "", false),
            r1 = new Btn(R1, "R1", "", false), r2 = new Btn(R2, "R2", "", false),
            select = new Btn(SELECT, "SELECT", "", false), start = new Btn(START, "START", "", false),
            l3 = new Btn(L3, "L3", "", false), r3 = new Btn(R3, "R3", "", false),
            boost = new Btn(BOOST, "BOOST", "", false);
    private final Btn[] base = { a, b, x, y, l1, l2, r1, r2, select, start, boost },
            withExtra = { a, b, x, y, l1, l2, r1, r2, select, start, l3, r3, boost };

    // the boosters' row (BOOST): CompanionView's commands and the state keys that say they are on
    private static final String[] BOOST_NAMES = { "High speed", "No battles", "Battle assist", "9999 damage" };
    private static final int[] BOOST_CMDS = { 1, 4, 5, 6 };
    private static final String[] BOOST_KEYS = { "speed", "noenc", "bplus", "dmg" };
    private final RectF[] boostRects = { new RectF(), new RectF(), new RectF(), new RectF() };
    private boolean boostOpen;
    private final Runnable boostRefresh = new Runnable() {
        @Override public void run() { if (boostOpen) { invalidate(); postDelayed(this, 500); } }
    };
    private Btn[] buttons = base;

    // Buttons shown: what each entry of the list is called in the "touch_hidden" setting (the hidden
    // ones, separated by commas) and on the screen
    static final String[] NAMES = { "DPAD", "A", "B", "X", "Y", "L1", "L2", "R1", "R2", "SELECT", "START", "BOOST", "L3", "R3", "STICK" };
    static final String[] TITLES = { "D-pad or left stick", "A (OK)", "B (Back)", "X", "Y (Menu)", "L1", "L2", "R1", "R2", "SELECT", "START",
            "BOOST", "L3", "R3", "Right stick" };
    /** How many of them there are without the right stick, L3 and R3. */
    static final int NAMES_BASE = 12;
    private boolean padHidden, stickHidden;

    static java.util.Set<String> hiddenSet(String hidden) {
        java.util.Set<String> s = new java.util.LinkedHashSet<>();
        if (hidden != null) for (String n : hidden.split(",")) if (!n.trim().isEmpty()) s.add(n.trim());
        return s;
    }

    /** Hides these buttons ("touch_hidden"). */
    void setHidden(String hidden) {
        java.util.Set<String> s = hiddenSet(hidden);
        padHidden = s.contains("DPAD");
        stickHidden = s.contains("STICK");
        for (Btn btn : withExtra) btn.hidden = s.contains(btn.label);
        if (stickHidden) release();
        if (padHidden) releaseLeft();
        if (boost.hidden) { boostOpen = false; removeCallbacks(boostRefresh); }
        if (getWidth() > 0) { place(getWidth(), getHeight()); invalidate(); }
    }

    /** A button, a stick or an open booster is there. */
    boolean takes(float px, float py) { return hit(px, py) != 0 || onStick(px, py) || onPad(px, py) || inBoosters(px, py); }

    private boolean stick() { return extra && !stickHidden; }

    // the right stick: its ring, the finger that holds it and where the knob is (-1..1)
    private boolean extra;
    private float stickX, stickY, stickR;
    private int stickPointer = -1;
    private float knobX, knobY;
    // the left stick (in the D-pad's place when leftStick is on)
    private boolean leftStick;
    private int padPointer = -1;
    private float lKnobX, lKnobY;

    private final Listener listener;
    private final Paint line = new Paint(Paint.ANTI_ALIAS_FLAG), fill = new Paint(Paint.ANTI_ALIAS_FLAG),
            text = new Paint(Paint.ANTI_ALIAS_FLAG), small = new Paint(Paint.ANTI_ALIAS_FLAG);
    private float padX, padY, padR, unit;
    private int state;

    // the four groups that move together
    static final int G_PAD = 0, G_FACE = 1, G_LEFT = 2, G_RIGHT = 3, G_STICK = 4, GROUPS = 5;
    private float scale = 1f;
    private final float[] groupOff = new float[2 * GROUPS];   // each group's offset, as fractions of the width / height
    private boolean editing;
    private int dragGroup = -1;
    private float dragX, dragY;
    private Runnable onEdited;

    /** Shows the right stick, L3 and R3. */
    void setExtra(boolean on) {
        extra = on;
        buttons = on ? withExtra : base;
        if (!on) release();
        if (getWidth() > 0) { place(getWidth(), getHeight()); invalidate(); }
    }
    boolean extra() { return extra; }

    /** The D-pad's ring as an analog left stick. */
    void setLeftStick(boolean on) {
        leftStick = on;
        releaseLeft();
        invalidate();
    }
    boolean leftStick() { return leftStick; }

    private int groups() { return extra ? GROUPS : 4; }

    private void release() {
        stickPointer = -1;
        knobX = knobY = 0;
    }

    private void releaseLeft() {
        padPointer = -1;
        lKnobX = lKnobY = 0;
    }

    /** "scale;opacity;padX,padY;faceX,faceY;leftX,leftY;rightX,rightY[;stickX,stickY]" (Settings > On-screen controls). */
    void setLayout(String layout) {
        java.util.Arrays.fill(groupOff, 0f);
        scale = 1f;
        float alpha = 1f;
        if (layout != null && !layout.isEmpty()) {
            try {
                String[] p = layout.split(";");
                scale = Math.max(0.5f, Math.min(2f, Float.parseFloat(p[0])));
                alpha = Math.max(0.15f, Math.min(1f, Float.parseFloat(p[1])));
                for (int g = 0; g < GROUPS && 2 + g < p.length; g++) {
                    String[] xy = p[2 + g].split(",");
                    groupOff[2 * g] = Float.parseFloat(xy[0]);
                    groupOff[2 * g + 1] = Float.parseFloat(xy[1]);
                }
            } catch (RuntimeException ignored) { }   // a damaged setting: the default layout
        }
        setAlpha(alpha);
        if (getWidth() > 0) { place(getWidth(), getHeight()); invalidate(); }
    }

    String layout() {
        StringBuilder b = new StringBuilder();
        b.append(String.format(java.util.Locale.ROOT, "%.2f;%.2f", scale, getAlpha()));
        for (int g = 0; g < GROUPS; g++) b.append(String.format(java.util.Locale.ROOT, ";%.4f,%.4f", groupOff[2 * g], groupOff[2 * g + 1]));
        return b.toString();
    }

    void setScale(float s) { scale = s; if (getWidth() > 0) { place(getWidth(), getHeight()); invalidate(); } }
    float scale() { return scale; }
    void resetPositions() { java.util.Arrays.fill(groupOff, 0f); if (getWidth() > 0) { place(getWidth(), getHeight()); invalidate(); } }

    /** Edit mode: fingers drag the groups; edited runs after each drag. */
    void setEditing(boolean on, Runnable edited) { editing = on; onEdited = edited; invalidate(); }

    TouchControls(Context c, Listener l) {
        super(c);
        listener = l;
        line.setStyle(Paint.Style.STROKE);
        line.setColor(0x80FFFFFF);
        fill.setStyle(Paint.Style.FILL);
        text.setColor(0xB0FFFFFF);
        text.setTextAlign(Paint.Align.CENTER);
        text.setTypeface(Typeface.DEFAULT_BOLD);
        small.setColor(0x90FFFFFF);
        small.setTextAlign(Paint.Align.CENTER);
        setHapticFeedbackEnabled(true);
    }

    @Override
    protected void onSizeChanged(int w, int h, int ow, int oh) { place(w, h); }

    private void place(int w, int h) {
        unit = Math.min(w, h) / 8f * scale;
        line.setStrokeWidth(Math.max(2f, unit * 0.04f));
        text.setTextSize(unit * 0.38f);
        small.setTextSize(unit * 0.24f);
        // D-pad, bottom left, close to the corner 
        padR = unit * 1.25f;
        padX = unit * 0.3f + padR;
        padY = h - unit * 0.3f - padR;
        // face buttons, bottom right
        float br = unit * 0.5f, off = unit * 0.85f;
        float fx = w - unit * 0.3f - off - br, fy = h - unit * 0.3f - off - br;
        circle(a, fx, fy + off, br);
        circle(b, fx + off, fy, br);
        circle(x, fx - off, fy, br);
        circle(y, fx, fy - off, br);
        // shoulders, top corners, side by side (L2 L1 ... R1 R2)
        float sw = unit * 1.4f, sh = unit * 0.6f, m = unit * 0.25f, g = unit * 0.15f;
        l2.r.set(m, m, m + sw, m + sh);
        l1.r.set(m + sw + g, m, m + 2 * sw + g, m + sh);
        r2.r.set(w - m - sw, m, w - m, m + sh);
        r1.r.set(w - m - 2 * sw - g, m, w - m - sw - g, m + sh);
        // select / start, below the shoulders at the sides 
        float pw = unit * 1.4f, ph = unit * 0.5f, py = m + sh + unit * 0.25f;
        select.r.set(m, py, m + pw, py + ph);
        start.r.set(w - m - pw, py, w - m, py + ph);
        // L3 / R3 under SELECT / START, in their groups
        float py3 = py + ph + unit * 0.2f;
        l3.r.set(m, py3, m + pw, py3 + ph);
        r3.r.set(w - m - pw, py3, w - m, py3 + ph);
        // BOOST under START (under R3 with the extras)
        float pyb = (extra ? py3 : py) + ph + unit * 0.2f;
        boost.r.set(w - m - pw, pyb, w - m, pyb + ph);
        // the boosters' row: top middle, between the shoulders
        float bw = unit * 1.9f, bh = unit * 0.75f, gap = unit * 0.15f, rx = (w - 4 * bw - 3 * gap) / 2f, ry = m;
        if (rx < l1.r.right + gap) { bw = (w - 2 * (l1.r.right + gap) - 3 * gap) / 4f; rx = l1.r.right + gap; }
        for (int i = 0; i < 4; i++) boostRects[i].set(rx + i * (bw + gap), ry, rx + i * (bw + gap) + bw, ry + bh);
        // the right stick, above the face buttons
        stickR = unit * 0.85f;
        stickX = fx;
        stickY = fy - off - br - unit * 0.35f - stickR;
        // each group moved by its offset, then kept on the screen (a bigger size can push it off)
        for (int gi = 0; gi < groups(); gi++) {
            shift(gi, groupOff[2 * gi] * w, groupOff[2 * gi + 1] * h);
            RectF r = bounds(gi);
            if (r == null) continue;
            float dx = r.left < 0 ? -r.left : r.right > w ? w - r.right : 0;
            float dy = r.top < 0 ? -r.top : r.bottom > h ? h - r.bottom : 0;
            if (dx != 0 || dy != 0) {
                shift(gi, dx, dy);
                groupOff[2 * gi] += dx / w;
                groupOff[2 * gi + 1] += dy / h;
            }
        }
    }

    private Btn[] members(int g) {
        switch (g) {
            case G_FACE: return new Btn[] { a, b, x, y };
            case G_LEFT: return extra ? new Btn[] { l2, l1, select, l3 } : new Btn[] { l2, l1, select };
            case G_RIGHT: return extra ? new Btn[] { r1, r2, start, r3, boost } : new Btn[] { r1, r2, start, boost };
            default: return new Btn[0];
        }
    }

    private void shift(int g, float dx, float dy) {
        if (g == G_PAD) { padX += dx; padY += dy; return; }
        if (g == G_STICK) { stickX += dx; stickY += dy; return; }
        for (Btn btn : members(g)) btn.r.offset(dx, dy);
    }

    /** Where a group's shown buttons are; null when all of them are hidden. */
    private RectF bounds(int g) {
        if (g == G_PAD) return padHidden ? null : new RectF(padX - padR, padY - padR, padX + padR, padY + padR);
        if (g == G_STICK) return !stick() ? null : new RectF(stickX - stickR, stickY - stickR, stickX + stickR, stickY + stickR);
        RectF r = null;
        for (Btn btn : members(g)) { if (btn.hidden) continue; if (r == null) r = new RectF(btn.r); else r.union(btn.r); }
        return r;
    }

    private static void circle(Btn btn, float cx, float cy, float r) { btn.r.set(cx - r, cy - r, cx + r, cy + r); }

    @Override
    protected void onDraw(Canvas c) {
        if (editing) {
            fill.setColor(0x30FFD54F);
            for (int g = 0; g < groups(); g++) {
                RectF r = bounds(g);
                if (r == null) continue;
                r.inset(-unit * 0.15f, -unit * 0.15f);
                fill.setColor(g == dragGroup ? 0x60FFD54F : 0x30FFD54F);
                c.drawRoundRect(r, unit * 0.2f, unit * 0.2f, fill);
            }
        }
        // D-pad: a ring with four arrows; the pressed directions light up
        if (!padHidden) {
            fill.setColor(0x26FFFFFF);
            c.drawCircle(padX, padY, padR, fill);
            c.drawCircle(padX, padY, padR, line);
        }
        if (padHidden) { /* Buttons shown */ } else if (leftStick) {
            // the left stick: a knob that follows the finger
            float kr = padR * 0.45f, kx = padX + lKnobX * (padR - kr), ky = padY + lKnobY * (padR - kr);
            fill.setColor(padPointer >= 0 ? 0x90FFFFFF : 0x50FFFFFF);
            c.drawCircle(kx, ky, kr, fill);
            c.drawCircle(kx, ky, kr, line);
            c.drawText("L", kx, ky - (text.descent() + text.ascent()) / 2, text);
        } else {
            drawArrow(c, 0, -1, UP);
            drawArrow(c, 0, 1, DOWN);
            drawArrow(c, -1, 0, LEFT);
            drawArrow(c, 1, 0, RIGHT);
        }
        if (stick()) {
            // right stick: a ring and a knob that follows the finger
            fill.setColor(0x26FFFFFF);
            c.drawCircle(stickX, stickY, stickR, fill);
            c.drawCircle(stickX, stickY, stickR, line);
            float kr = stickR * 0.45f, kx = stickX + knobX * (stickR - kr), ky = stickY + knobY * (stickR - kr);
            fill.setColor(stickPointer >= 0 ? 0x90FFFFFF : 0x50FFFFFF);
            c.drawCircle(kx, ky, kr, fill);
            c.drawCircle(kx, ky, kr, line);
            c.drawText("R", kx, ky - (text.descent() + text.ascent()) / 2, text);
        }
        if (boostOpen) drawBoosters(c);
        for (Btn btn : buttons) {
            if (btn.hidden) continue;
            boolean on = (state & btn.bit) != 0 || (btn == boost && boostOpen);
            fill.setColor(on ? 0x90FFFFFF : 0x26FFFFFF);
            float cy = btn.r.centerY() - (text.descent() + text.ascent()) / 2;
            if (btn.round) {
                c.drawOval(btn.r, fill);
                c.drawOval(btn.r, line);
                if (!btn.hint.isEmpty()) {
                    c.drawText(btn.label, btn.r.centerX(), cy - unit * 0.08f, text);
                    c.drawText(btn.hint, btn.r.centerX(), cy + unit * 0.24f, small);
                } else {
                    c.drawText(btn.label, btn.r.centerX(), cy, text);
                }
            } else {
                float rr = btn.r.height() / 2;
                c.drawRoundRect(btn.r, rr, rr, fill);
                c.drawRoundRect(btn.r, rr, rr, line);
                Paint p = btn.label.length() > 2 ? small : text;
                c.drawText(btn.label, btn.r.centerX(), btn.r.centerY() - (p.descent() + p.ascent()) / 2, p);
            }
        }
    }

    /** The boosters' row, each with ON or OFF from the game's state. */
    private void drawBoosters(Canvas c) {
        org.json.JSONObject st = null;
        String js = GameActivity.companionState();
        if (js != null) try { st = new org.json.JSONObject(js); } catch (Exception ignored) { }
        for (int i = 0; i < 4; i++) {
            RectF r = boostRects[i];
            boolean on = st != null && st.optInt(BOOST_KEYS[i]) == 1;
            fill.setColor(on ? 0xB0C89B28 : 0x80000000);
            float rr = r.height() / 2;
            c.drawRoundRect(r, rr, rr, fill);
            c.drawRoundRect(r, rr, rr, line);
            c.drawText(BOOST_NAMES[i], r.centerX(), r.centerY() - unit * 0.04f, small);
            c.drawText(on ? "ON" : "OFF", r.centerX(), r.centerY() + unit * 0.24f, small);
        }
    }

    /** A finger came down at (px, py): BOOST opens or closes the row, a booster in it is turned on or off. */
    private boolean boostTap(float px, float py) {
        if (boostOpen) {
            for (int i = 0; i < 4; i++) {
                RectF r = boostRects[i];
                if (r.contains(px, py)) {
                    GameActivity.commandFromPanel(BOOST_CMDS[i]);
                    performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
                    removeCallbacks(boostRefresh);
                    postDelayed(boostRefresh, 150);
                    return true;
                }
            }
        }
        if (hit(px, py) == BOOST) {
            boostOpen = !boostOpen;
            performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
            removeCallbacks(boostRefresh);
            if (boostOpen) post(boostRefresh);
            invalidate();
            return true;
        }
        return false;
    }

    private boolean inBoosters(float px, float py) {
        if (!boostOpen) return false;
        for (RectF r : boostRects) if (r.contains(px, py)) return true;
        return false;
    }

    private void drawArrow(Canvas c, int dx, int dy, int bit) {
        float cx = padX + dx * padR * 0.6f, cy = padY + dy * padR * 0.6f, s = padR * 0.22f;
        android.graphics.Path p = new android.graphics.Path();
        // a triangle pointing (dx, dy)
        p.moveTo(cx + dx * s, cy + dy * s);
        p.lineTo(cx - dx * s + dy * s, cy - dy * s + dx * s);
        p.lineTo(cx - dx * s - dy * s, cy - dy * s - dx * s);
        p.close();
        fill.setColor((state & bit) != 0 ? 0xE0FFFFFF : 0x80FFFFFF);
        c.drawPath(p, fill);
    }

    @Override
    public boolean onTouchEvent(MotionEvent e) {
        if (editing) return drag(e);
        int s = 0;
        int lifted = -1;
        int act = e.getActionMasked();
        if (act == MotionEvent.ACTION_UP || act == MotionEvent.ACTION_POINTER_UP) lifted = e.getActionIndex();
        float oldKx = knobX, oldKy = knobY, oldLx = lKnobX, oldLy = lKnobY;
        boolean oldHeld = stickPointer >= 0, oldLHeld = padPointer >= 0;
        // the right stick: a finger that lands on it holds it until it lifts, wherever it slides
        if (act == MotionEvent.ACTION_DOWN || act == MotionEvent.ACTION_POINTER_DOWN) {
            int i = e.getActionIndex();
            boostTap(e.getX(i), e.getY(i));
        }
        if (act == MotionEvent.ACTION_CANCEL) { release(); releaseLeft(); }
        else if (leftStick && !padHidden) {
            // the left stick, as the right one: the finger that lands on the ring holds it
            if (act == MotionEvent.ACTION_DOWN || act == MotionEvent.ACTION_POINTER_DOWN) {
                int i = e.getActionIndex();
                if (padPointer < 0 && onPad(e.getX(i), e.getY(i))) padPointer = e.getPointerId(i);
            }
            if (lifted >= 0 && e.getPointerId(lifted) == padPointer) releaseLeft();
            int pi = padPointer >= 0 ? e.findPointerIndex(padPointer) : -1;
            if (pi >= 0) {
                float dx = (e.getX(pi) - padX) / (padR * 0.8f), dy = (e.getY(pi) - padY) / (padR * 0.8f);
                float d = (float) Math.hypot(dx, dy);
                if (d > 1f) { dx /= d; dy /= d; }
                lKnobX = dx; lKnobY = dy;
            } else if (padPointer >= 0) releaseLeft();
        }
        if (act != MotionEvent.ACTION_CANCEL && stick()) {
            if (act == MotionEvent.ACTION_DOWN || act == MotionEvent.ACTION_POINTER_DOWN) {
                int i = e.getActionIndex();
                if (stickPointer < 0 && onStick(e.getX(i), e.getY(i))) stickPointer = e.getPointerId(i);
            }
            if (lifted >= 0 && e.getPointerId(lifted) == stickPointer) release();
            int si = stickPointer >= 0 ? e.findPointerIndex(stickPointer) : -1;
            if (si >= 0) {
                float dx = (e.getX(si) - stickX) / (stickR * 0.8f), dy = (e.getY(si) - stickY) / (stickR * 0.8f);
                float d = (float) Math.hypot(dx, dy);
                if (d > 1f) { dx /= d; dy /= d; }
                knobX = dx; knobY = dy;
            } else if (stickPointer >= 0) release();
        }
        if (act != MotionEvent.ACTION_CANCEL) {
            for (int i = 0; i < e.getPointerCount(); i++) {
                if (i == lifted || (stickPointer >= 0 && e.getPointerId(i) == stickPointer)
                        || (padPointer >= 0 && e.getPointerId(i) == padPointer)) continue;
                if (inBoosters(e.getX(i), e.getY(i))) continue;
                s |= hit(e.getX(i), e.getY(i));
            }
        }
        boolean stickMoved = knobX != oldKx || knobY != oldKy || lKnobX != oldLx || lKnobY != oldLy;
        if (s != state || stickMoved) {
            if ((s & ~state & ~BOOST) != 0 || (stickPointer >= 0 && !oldHeld) || (padPointer >= 0 && !oldLHeld))
                performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
            state = s;
            // XInput: Y points up, the screen's down
            listener.pad(s & 0xFFFF, (s & L2) != 0 ? 255 : 0, (s & R2) != 0 ? 255 : 0,
                         Math.round(lKnobX * 32767f), Math.round(-lKnobY * 32767f),
                         Math.round(knobX * 32767f), Math.round(-knobY * 32767f));
            invalidate();
        }
        return true;
    }

    /** Edit mode: the first finger drags the group it lands on. */
    private boolean drag(MotionEvent e) {
        switch (e.getActionMasked()) {
            case MotionEvent.ACTION_DOWN: {
                dragGroup = -1;
                for (int g = 0; g < groups(); g++) {
                    RectF r = bounds(g);
                    if (r == null) continue;
                    r.inset(-unit * 0.3f, -unit * 0.3f);
                    if (r.contains(e.getX(), e.getY())) { dragGroup = g; break; }
                }
                dragX = e.getX(); dragY = e.getY();
                invalidate();
                return true;
            }
            case MotionEvent.ACTION_MOVE:
                if (dragGroup >= 0) {
                    groupOff[2 * dragGroup] += (e.getX() - dragX) / getWidth();
                    groupOff[2 * dragGroup + 1] += (e.getY() - dragY) / getHeight();
                    dragX = e.getX(); dragY = e.getY();
                    place(getWidth(), getHeight());
                    invalidate();
                }
                return true;
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                if (dragGroup >= 0 && onEdited != null) onEdited.run();
                dragGroup = -1;
                invalidate();
                return true;
        }
        return true;
    }

    /** Whether a finger at (px, py) takes the right stick. */
    private boolean onStick(float px, float py) {
        return stick() && Math.hypot(px - stickX, py - stickY) < stickR * 1.2f;
    }

    /** Whether a finger at (px, py) takes the left stick. */
    private boolean onPad(float px, float py) {
        return leftStick && !padHidden && Math.hypot(px - padX, py - padY) < padR * 1.35f;
    }

    /** What a finger at (px, py) presses. */
    private int hit(float px, float py) {
        if (onStick(px, py) || onPad(px, py)) return 0;
        float dx = px - padX, dy = py - padY;
        float d = (float) Math.hypot(dx, dy);
        if (!padHidden && d < padR * 1.35f) {
            if (d < padR * 0.2f) return 0;                       // dead centre
            double ang = Math.toDegrees(Math.atan2(dy, dx));     // 0 = right, 90 = down
            int bits = 0;
            if (ang > -67.5 && ang < 67.5) bits |= RIGHT;
            if (ang > 22.5 && ang < 157.5) bits |= DOWN;
            if (ang > 112.5 || ang < -112.5) bits |= LEFT;
            if (ang > -157.5 && ang < -22.5) bits |= UP;
            return bits;
        }
        Btn best = null;
        float bestD = Float.MAX_VALUE;
        for (Btn btn : buttons) {
            if (btn.hidden) continue;
            RectF r = btn.r;
            float grow = btn.round ? unit * 0.25f : unit * 0.15f;  // a little forgiveness around each
            if (px < r.left - grow || px > r.right + grow || py < r.top - grow || py > r.bottom + grow) continue;
            float dd = (float) Math.hypot(px - r.centerX(), py - r.centerY());
            if (dd < bestD) { bestD = dd; best = btn; }
        }
        return best != null ? best.bit : 0;
    }
}
