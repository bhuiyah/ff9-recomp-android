package com.ff9recomp;

import android.content.Context;
import android.content.SharedPreferences;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.LinearGradient;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.RectF;
import android.graphics.Shader;
import android.graphics.Typeface;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.view.HapticFeedbackConstants;
import android.view.MotionEvent;
import android.view.View;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;

/**
 * The second screen (AYN Thor's bottom screen). A header (where, play time, gil, the guide book),
 * a row of tabs, the party on the left (hideable) with the tab's panel beside it, and the command
 * deck: Menu, Save, Load, Speed, Battles, Battle+.
 *
 * Tabs: Auto (enemies in battle, the map on the world map, items in fields), Abilities (what the
 * equipped gear still teaches, with AP), Map (the world map, the party's arrow, the chocobo and the
 * airship), Items, Key items and Extras (the four boosters by touch, the Excalibur II countdown and
 * the counters the game keeps but never shows). In battle the enemy panel shows what Scan would:
 * elements, what is left to steal and the drops.
 *
 * The game sends its state ten times a second (FF9Engine Core/Companion.cs). Blank until a game is
 * under way; cutscenes dim it with a slow ease. FF9's menu-window colours. Portraits, item icons
 * and the world map are saved on the device from the player's own copy; none ship with the app.
 */
public class CompanionView extends View {
    // FF9's menu windows: the game's own window skin (its paper texture and silver frame, cut from
    // the player's copy at runtime); before it arrives, FF9's grey-blue stone with a cream frame.
    // Gold marks what is selected or on, as the game's own cursor does.
    private static final int BG = Color.BLACK, TEXT = 0xFFF2EEE2, LABEL = 0xFFB8BAC6, GOLD = 0xFFF0CF72, OK = 0xFF9EE0A8;
    private static final int WIN_A = 0xFF6A7088, WIN_B = 0xFF454A5E, WIN_C = 0xFF2A2D3A, BORDER = 0xFFDCD6C4, INNER = 0xFF3A3F50,
            ROW = 0x40FFFFFF, TAB_BG = 0xFF30333F, TAB_EDGE = 0xFF8A8FA2, TAB_TEXT = 0xFFE4E0D4,
            GOLD_A = 0xFFE7C66C, GOLD_B = 0xFFB98E2E, GOLD_EDGE = 0xFFF6E2A0, GOLD_INK = 0xFF2B1D02,
            BTN_A = 0xFF5E6478, BTN_B = 0xFF22252F, HOT_A = 0xFF8A8466, HOT_B = 0xFF3E3A2A, TRACK = 0xC0101218;
    private static final int[] HP = { 0xFF4FBF6A, 0xFF9BE38A }, MP = { 0xFF3D8EE0, 0xFF8CC8FF }, ATB = { 0xFFD8B840, 0xFFFFF08A },
            TRANCE = { 0xFFE04F9A, 0xFFFF9CCC }, LEARNT = { 0xFFD8B840, 0xFFFFF08A }, LOW = { 0xFFD87A30, 0xFFF0B060 };
    private static final int[][] CHIP = { { 0xFF5A1C1C, 0xFFFFB3A8, 0xFFD46A5A }, { 0xFF163A4A, 0xFF9FE0FF, 0xFF4AA0C8 },
            { 0xFF2A2A40, 0xFFC8C8E0, 0xFF6A6A90 }, { 0xFF2A3A20, 0xFFC8E0A8, 0xFF7A9A5A } };

    // the deck's commands (Core/Companion.cs; Save and Load go through the app's save states)
    private static final int CMD_SPEED = 1, CMD_SAVE = 2, CMD_LOAD = 3, CMD_BATTLES = 4, CMD_BPLUS = 5, CMD_DMG = 6, CMD_MENU = 100;

    private static final int TAB_AUTO = 0, TAB_ABIL = 1, TAB_MAP = 2, TAB_ITEMS = 3, TAB_KEYS = 4, TAB_EXTRA = 5;
    private static final String[] TAB_NAMES = { "Auto", "Abilities", "Map", "Items", "Key items", "Extras" };

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Paint fill = new Paint(Paint.ANTI_ALIAS_FLAG), line = new Paint(Paint.ANTI_ALIAS_FLAG),
            text = new Paint(Paint.ANTI_ALIAS_FLAG), bmpPaint = new Paint(Paint.FILTER_BITMAP_FLAG | Paint.ANTI_ALIAS_FLAG);
    private Typeface sans = Typeface.create("sans-serif", Typeface.NORMAL), sansBold = Typeface.create("sans-serif-medium", Typeface.NORMAL);
    /** The game's own text font, which the engine copies from the player's game files when it first
     *  loads it (Text/Fonts.cs ShareWithPanels): the panels write in it once it is there. */
    private boolean gameFont;
    private long fontChecked;

    private void loadGameFont() {
        if (gameFont || SystemClock.uptimeMillis() - fontChecked < 1000) return;
        fontChecked = SystemClock.uptimeMillis();
        java.io.File f = new java.io.File(new java.io.File(new GameInstall(getContext()).files, "worldmap"), "game_font.ttf");   // FF9_WORLDMAP_DIR
        if (!f.isFile()) return;
        try {
            Typeface tf = Typeface.createFromFile(f);
            sans = tf;
            sansBold = tf;      // the game's font is a bold one already
            gameFont = true;
            invalidate();
        } catch (RuntimeException e) {
            gameFont = true;    // unreadable: keep the system font, don't try again
            android.util.Log.w("FF9Companion", "game font", e);
        }
    }
    private JSONObject state = new JSONObject();
    private String last = "";
    private final SharedPreferences prefs;

    /** Anything on the screen that can be touched. */
    private static final class Hit {
        final RectF box = new RectF(); final int kind, arg;
        Hit(RectF r, int kind, int arg) { box.set(r); this.kind = kind; this.arg = arg; }
    }
    private static final int H_CMD = 0, H_TAB = 1, H_HIDE = 2, H_BOOK = 3, H_PANELS = 4;
    private final List<Hit> hits = new ArrayList<>();
    private Hit pressed;

    private static final String[] DECK = { "Menu", "Save", "Load", "Speed", "Battles", "Battle+" };
    private static final int[] DECK_CMD = { CMD_MENU, CMD_SAVE, CMD_LOAD, CMD_SPEED, CMD_BATTLES, CMD_BPLUS };
    private boolean deck;
    private int tab;
    private boolean partyHidden;
    /** Side by side (GameActivity.arrangeSide): the header's button that hides the panels (null:
     *  none), at the header's left or right end, the edge next to the game. */
    private Runnable onHide;
    private boolean hideAtLeft;
    void setOnHide(Runnable r, boolean atLeft) { onHide = r; hideAtLeft = atLeft; invalidate(); }

    /** A tall, narrow area (Side by side: the panels beside the game on one screen). The panels then
     *  stack: a two-row header, tabs and deck in two rows, the party above the details. */
    private boolean narrow() { return getWidth() < getHeight() * 0.8f; }
    private String flash;
    private long flashUntil;

    // a list panel scrolls by dragging
    private final RectF panelBox = new RectF();
    private float scroll, contentH, downX, downY, downScroll;
    private boolean dragging;
    private int scrolledTab = -1;

    // cutscene dim, as FF7Recomp: a state holds a moment before the lights change, then eases
    private static final float DIM_MAX = 0.85f;
    private static final long DIM_HOLD_IN = 250, DIM_HOLD_OUT = 400, DIM_IN_MS = 1200, DIM_OUT_MS = 700;
    private float dim;
    private boolean dimTarget, seenCine, dimOn = true;
    private long seenSince;
    private android.animation.ValueAnimator dimAnim;

    private final Runnable poll = new Runnable() {
        @Override public void run() {
            String s = GameActivity.companionState();
            if (s != null && !s.equals(last)) {
                last = s;
                try { state = new JSONObject(s); } catch (Exception ignored) { }
                invalidate();
            }
            updateDim(dimOn && state.optInt("cine") == 1);
            loadGameFont();
            if (flash != null && SystemClock.uptimeMillis() > flashUntil) { flash = null; invalidate(); }
            if (GuideInstaller.busy || System.currentTimeMillis() < GuideInstaller.statusUntil) invalidate();   // its progress
            handler.postDelayed(this, 100);
        }
    };

    public CompanionView(Context c) {
        super(c);
        line.setStyle(Paint.Style.STROKE);
        setHapticFeedbackEnabled(true);
        prefs = Settings.prefs(c);
        deck = prefs.getBoolean("bottom_buttons", true);
        dimOn = prefs.getBoolean("panel_dim", true);   // Settings > Screens (GitHub issue 16)
        tab = Math.max(0, Math.min(TAB_EXTRA, prefs.getInt("deck_tab", TAB_AUTO)));
        partyHidden = prefs.getBoolean("deck_party_hidden", false);
    }

    @Override protected void onAttachedToWindow() { super.onAttachedToWindow(); handler.post(poll); }
    @Override protected void onDetachedFromWindow() { handler.removeCallbacks(poll); super.onDetachedFromWindow(); }

    private void updateDim(boolean cine) {
        long now = SystemClock.uptimeMillis();
        if (cine != seenCine) { seenCine = cine; seenSince = now; }
        if (cine == dimTarget || now - seenSince < (cine ? DIM_HOLD_IN : DIM_HOLD_OUT)) return;
        dimTarget = cine;
        if (dimAnim != null) dimAnim.cancel();
        dimAnim = android.animation.ValueAnimator.ofFloat(dim, cine ? 1f : 0f);
        dimAnim.setDuration((long) ((cine ? DIM_IN_MS : DIM_OUT_MS) * Math.abs((cine ? 1f : 0f) - dim)));
        dimAnim.setInterpolator(new android.view.animation.PathInterpolator(0.45f, 0f, 0.2f, 1f));
        dimAnim.addUpdateListener(a -> { dim = (float) a.getAnimatedValue(); invalidate(); });
        dimAnim.start();
    }

    /** The game's text tags (like [STRT=67,1] and [ENDN]) out of a name. */
    static String clean(String s) { return s == null ? "" : s.replaceAll("\\[[A-Z]+(=[^\\]]*)?\\]", "").trim(); }

    // ------------------------------------------------------------------ images saved from the player's copy

    private final HashMap<String, Bitmap> images = new HashMap<>();
    private final HashMap<String, Long> missing = new HashMap<>();

    private Bitmap image(String name) {
        if (name == null || name.isEmpty()) return null;
        Bitmap b = images.get(name);
        if (b != null) return b;
        Long since = missing.get(name);
        long now = SystemClock.uptimeMillis();
        if (since != null && now - since < 2000) return null;
        File f = new File(new GameInstall(getContext()).files, "worldmap/" + name + ".png");
        b = f.isFile() ? BitmapFactory.decodeFile(f.getPath()) : null;
        if (b != null) { images.put(name, b); missing.remove(name); }
        else missing.put(name, now);
        return b;
    }

    // ------------------------------------------------------------------ drawing

    @Override
    protected void onDraw(Canvas c) {
        int w = getWidth(), h = getHeight();
        c.drawColor(BG);
        hits.clear();
        if (state.optInt("ready") != 1) return;     // blank until a game is under way
        final boolean nar = narrow();
        float u = nar ? Math.min(h / 1500f, w / 620f) : Math.min(w, h) / 1080f, pad = 14 * u, gap = 12 * u;
        String mode = state.optString("mode");
        boolean battle = "battle".equals(mode), world = "world".equals(mode);

        // header: where, play time, gil, the guide book (narrow: where and the book, then time and gil)
        RectF head = new RectF(pad, pad, w - pad, pad + (nar ? 150 : 96) * u);
        window(c, head, u);
        String loc = clean(state.optString("loc"));
        String where = battle ? "Battle" : loc.isEmpty() ? (world ? "World map" : " ") : loc;
        float rowB = nar ? head.top + 84 * u : head.bottom;     // the first row's bottom
        float hl = head.left, hr = head.right;                   // the first row's room
        if (onHide != null) {          // Side by side: hide the panels, at the edge next to the game
            float hs = Math.min(rowB - head.top - 24 * u, 60 * u);
            float hx = hideAtLeft ? head.left + 14 * u : head.right - 14 * u - hs;
            RectF hb = new RectF(hx, head.top + (rowB - head.top - hs) / 2, hx + hs, head.top + (rowB - head.top + hs) / 2);
            drawHideButton(c, hb, pressed != null && pressed.kind == H_PANELS, u);
            hits.add(new Hit(new RectF(hb.left - 10 * u, head.top, hb.right + 10 * u, rowB), H_PANELS, 0));
            if (hideAtLeft) hl = hb.right; else hr = hb.left - 6 * u;
        }
        float right = hr - 24 * u;
        {   // the book: Pixel Guide on this screen (or the player's own guide page, when set)
            RectF book = new RectF(right - 64 * u, head.top + 20 * u, right, rowB - 20 * u);
            boolean hot = pressed != null && pressed.kind == H_BOOK;
            if (hot) {
                fill.setShader(null);
                fill.setColor(0x55FFFFFF);
                c.drawRoundRect(new RectF(book.left - 8 * u, book.top - 6 * u, book.right + 8 * u, book.bottom + 6 * u), 10 * u, 10 * u, fill);
            }
            drawBook(c, book, hot, u);
            if (SystemClock.uptimeMillis() - guideCheckedAt > 5000) refreshGuide();
            if (guideIntent == null && !GameActivity.hasGuide()) {   // not installed: a download badge
                float br = 13 * u, bx = book.right, by = book.bottom;
                fill.setShader(null);
                fill.setColor(GuideInstaller.busy ? 0xFFE0B020 : 0xFF40B060);
                c.drawCircle(bx, by, br, fill);
                line.setColor(Color.WHITE);
                line.setStrokeWidth(3 * u);
                c.drawLine(bx, by - br * 0.55f, bx, by + br * 0.45f, line);
                c.drawLine(bx - br * 0.45f, by, bx, by + br * 0.45f, line);
                c.drawLine(bx + br * 0.45f, by, bx, by + br * 0.45f, line);
            }
            hits.add(new Hit(new RectF(book.left - 12 * u, head.top, book.right + 16 * u, rowB), H_BOOK, 0));
            right = book.left - 34 * u;
        }
        String gs = GuideInstaller.status;
        if (gs != null && System.currentTimeMillis() < GuideInstaller.statusUntil) { flash = gs; flashUntil = SystemClock.uptimeMillis() + 500; }
        String gil = String.format(Locale.US, "%,d G", state.optLong("gil"));
        long t = state.optLong("time");
        String time = String.format(Locale.US, "%d:%02d:%02d", t / 3600, t / 60 % 60, t % 60);
        if (nar) {
            text.setTypeface(sansBold);
            label(c, ellipsize(where, right - hl - 60 * u, 38 * u), hl + 22 * u, head.top + 56 * u, 38 * u, TEXT, Paint.Align.LEFT);
            text.setTypeface(sans);
            float y = head.bottom - 24 * u;
            label(c, "Time", head.left + 22 * u, y, 22 * u, LABEL, Paint.Align.LEFT);
            text.setTextSize(22 * u);
            label(c, time, head.left + 22 * u + text.measureText("Time ") + 6 * u, y, 32 * u, TEXT, Paint.Align.LEFT);
            label(c, gil, head.right - 22 * u, y, 32 * u, GOLD, Paint.Align.RIGHT);
            text.setTextSize(32 * u);
            label(c, "Gil", head.right - 22 * u - text.measureText(gil) - 12 * u, y, 22 * u, LABEL, Paint.Align.RIGHT);
        } else {
            text.setTypeface(sans);
            text.setTextSize(32 * u);
            float gilW = Math.max(text.measureText(gil), 120 * u);
            label(c, "Gil", right - gilW, head.top + 36 * u, 22 * u, LABEL, Paint.Align.LEFT);
            label(c, gil, right - gilW, head.top + 76 * u, 32 * u, GOLD, Paint.Align.LEFT);
            float tx = right - gilW - 40 * u - Math.max(text.measureText(time), 130 * u);
            label(c, "Time", tx, head.top + 36 * u, 22 * u, LABEL, Paint.Align.LEFT);
            label(c, time, tx, head.top + 76 * u, 32 * u, TEXT, Paint.Align.LEFT);
            text.setTypeface(sansBold);
            label(c, ellipsize(where, tx - hl - 60 * u, 38 * u), hl + 22 * u, head.centerY() + 13 * u, 38 * u, TEXT, Paint.Align.LEFT);
        }

        // tabs
        // narrow: two rows of three, the party's « at the second row's end
        int tabRows = nar ? 2 : 1, perRow = 6 / tabRows;
        float rowH = 64 * u, tabTop = head.bottom + gap, tabH = tabRows * rowH + (tabRows - 1) * 8 * u, hideW = 70 * u;
        float tw = (w - 2 * pad - hideW - perRow * 8 * u) / perRow;
        float twFirst = nar ? (w - 2 * pad - (perRow - 1) * 8 * u) / perRow : tw;   // narrow: the first row has no «
        for (int i = 0; i < 6; i++) {
            int row = i / perRow, col = i % perRow;
            float cw = row == 0 ? twFirst : tw, ry = tabTop + row * (rowH + 8 * u);
            RectF r = new RectF(pad + col * (cw + 8 * u), ry, pad + col * (cw + 8 * u) + cw, ry + rowH);
            boolean sel = i == tab;
            boolean hot = pressed != null && pressed.kind == H_TAB && pressed.arg == i;
            tabShape(c, r, sel, hot, u);
            text.setTypeface(sel ? sansBold : sans);
            label(c, TAB_NAMES[i], r.centerX(), r.centerY() + 10 * u, fit(TAB_NAMES[i], r.width() - 14 * u, 27 * u), sel ? GOLD_INK : TAB_TEXT, Paint.Align.CENTER);
            hits.add(new Hit(r, H_TAB, i));
        }
        RectF hide = new RectF(w - pad - hideW, tabTop + tabH - rowH, w - pad, tabTop + tabH);
        tabShape(c, hide, false, pressed != null && pressed.kind == H_HIDE, u);
        label(c, nar ? (partyHidden ? "▾" : "▴") : partyHidden ? "»" : "«", hide.centerX(), hide.centerY() + 12 * u, 36 * u, TAB_TEXT, Paint.Align.CENTER);
        hits.add(new Hit(hide, H_HIDE, 0));

        // the deck at the bottom
        int deckRows = nar ? 2 : 1, deckPer = DECK.length / deckRows;
        float deckH = deck ? deckRows * 104 * u + (deckRows - 1) * 10 * u : 0;
        if (deck) {
            float bw = (w - 2 * pad - (deckPer - 1) * 10 * u) / deckPer, top0 = h - pad - deckH;
            for (int i = 0; i < DECK.length; i++) {
                float bx = pad + (i % deckPer) * (bw + 10 * u), by = top0 + (i / deckPer) * (104 * u + 10 * u);
                RectF r = new RectF(bx, by, bx + bw, by + 104 * u);
                int cmd = DECK_CMD[i];
                boolean on = isOn(cmd);
                boolean hot = pressed != null && pressed.kind == H_CMD && pressed.arg == cmd && pressed.box.equals(r);
                button(c, r, on, hot, u);
                String l = DECK[i];
                if (cmd == CMD_SPEED && on) l = "Speed " + speedFactor() + "×";
                else if (cmd == CMD_BATTLES && on) l = "No battles";
                text.setTypeface(sansBold);
                deckIcon(c, cmd, r.centerX(), r.top + 36 * u, on ? GOLD : TEXT, u);
                label(c, l, r.centerX(), r.bottom - 22 * u, fit(l, r.width() - 16 * u, 26 * u), on ? GOLD : TEXT, Paint.Align.CENTER);
                hits.add(new Hit(r, H_CMD, cmd));
            }
        }

        // body: the party column and the tab's panel
        float top = tabTop + tabH + gap, bottom = h - pad - deckH - (deck ? gap : 0);
        RectF panel;
        if (nar) {                     // the party across the top, the details below
            JSONArray members = state.optJSONArray("party");
            int slots = Math.max(1, Math.min(4, members == null ? 0 : members.length()));   // no empty cards
            float partyH = partyHidden ? 0 : Math.min(slots * 170 * u + (slots - 1) * 10 * u, (bottom - top) * 0.5f);
            if (!partyHidden) drawParty(c, new RectF(pad, top, w - pad, top + partyH), battle, slots, u);
            panel = new RectF(pad, top + (partyHidden ? 0 : partyH + gap), w - pad, bottom);
        } else {
            float colW = partyHidden ? 0 : 410 * u;
            if (!partyHidden) drawParty(c, new RectF(pad, top, pad + colW, bottom), battle, 4, u);
            panel = new RectF(pad + (partyHidden ? 0 : colW + gap), top, w - pad, bottom);
        }
        window(c, panel, u);
        panelBox.set(panel);
        int show = tab;
        if (tab == TAB_AUTO) show = battle ? -1 : world ? TAB_MAP : TAB_ITEMS;
        if (show != scrolledTab) { scrolledTab = show; scroll = 0; }
        RectF in = new RectF(panel.left + 20 * u, panel.top + 14 * u, panel.right - 20 * u, panel.bottom - 14 * u);
        c.save();
        c.clipRect(panel.left + 4 * u, panel.top + 4 * u, panel.right - 4 * u, panel.bottom - 4 * u);
        contentH = 0;
        switch (show) {
            case -1: if (battle) drawEnemies(c, in, u); else drawItems(c, in, u); break;
            case TAB_ABIL: drawAbilities(c, in, u); break;
            case TAB_MAP: drawMap(c, in, world, loc, u); break;
            case TAB_ITEMS: drawItems(c, in, u); break;
            case TAB_KEYS: drawKeys(c, in, u); break;
            case TAB_EXTRA: drawExtras(c, in, u); break;
        }
        c.restore();
        if (show == TAB_ABIL || show == TAB_ITEMS || show == TAB_KEYS || (show == -1 && battle)) {
            float max = Math.max(0, contentH - in.height());
            if (scroll > max) scroll = max;
            if (max > 0) {   // a thin scroll bar
                float trackH = in.height(), th = Math.max(40 * u, trackH * in.height() / contentH), ty = in.top + (trackH - th) * (scroll / max);
                fill.setShader(null);
                fill.setColor(0x60C7CFE3);
                c.drawRoundRect(panel.right - 12 * u, ty, panel.right - 7 * u, ty + th, 3 * u, 3 * u, fill);
            }
        } else scroll = 0;

        if (flash != null) {
            text.setTypeface(sansBold);
            text.setTextSize(30 * u);
            float fw = text.measureText(flash) + 48 * u;
            RectF fr = new RectF(panel.centerX() - fw / 2, panel.bottom - 84 * u, panel.centerX() + fw / 2, panel.bottom - 24 * u);
            button(c, fr, true, false, u);
            label(c, flash, fr.centerX(), fr.centerY() + 10 * u, 30 * u, GOLD, Paint.Align.CENTER);
        }
        if (dim > 0) {
            fill.setShader(null);
            fill.setColor(Color.argb(Math.round(255 * DIM_MAX * dim), 0, 0, 0));
            c.drawRect(0, 0, w, h, fill);
        }
    }

    private boolean isOn(int cmd) {
        switch (cmd) {
            case CMD_SPEED: return state.optInt("speed") == 1;
            case CMD_BATTLES: return state.optInt("noenc") == 1;
            case CMD_BPLUS: return state.optInt("bplus") == 1;
            case CMD_DMG: return state.optInt("dmg") == 1;
        }
        return false;
    }

    private int speedFactor() { return Settings.get(getContext(), Settings.find("speed")) + 2; }

    // ---- party column

    private static final int[] FACE_TINT = { 0xFF6A4A2A, 0xFF2A2A5A, 0xFF4A5A6A, 0xFF6A2A3A, 0xFF5A3A6A, 0xFF7A3A1A, 0xFF3A6A5A, 0xFF6A5A2A };

    private void drawParty(Canvas c, RectF col, boolean battle, int slots, float u) {
        JSONArray party = state.optJSONArray("party");
        int n = party == null ? 0 : Math.min(slots, party.length());
        float g = 10 * u, ch = (col.height() - g * (slots - 1)) / slots;
        for (int i = 0; i < slots; i++) {
            RectF r = new RectF(col.left, col.top + i * (ch + g), col.right, col.top + i * (ch + g) + ch);
            window(c, r, u);
            if (i >= n) continue;
            JSONObject m = party.optJSONObject(i);
            if (m == null) continue;
            String name = clean(m.optString("name"));
            float k = Math.min(1f, ch / (180 * u));
            float fs = Math.min(96 * u, ch - 30 * u) * Math.max(0.6f, k);
            Bitmap fb = image(m.optString("face"));
            // the game's portrait at its own proportions (fitted, never stretched)
            float fw = fs, fh = fs;
            if (fb != null && fb.getWidth() > 0 && fb.getHeight() > 0) {
                float ar = fb.getWidth() / (float) fb.getHeight();
                if (ar >= 1) fh = fs / ar; else fw = fs * ar;
            }
            RectF face = new RectF(r.left + 16 * u + (fs - fw) / 2, r.centerY() - fh / 2, r.left + 16 * u + (fs + fw) / 2, r.centerY() + fh / 2);
            if (fb != null) {
                c.drawBitmap(fb, null, face, bmpPaint);
            } else {
                fill.setShader(null);
                fill.setColor(FACE_TINT[Math.abs(name.hashCode()) % FACE_TINT.length]);
                c.drawRoundRect(face, 10 * u, 10 * u, fill);
                text.setTypeface(sansBold);
                label(c, name.isEmpty() ? "?" : name.substring(0, 1), face.centerX(), face.centerY() + fs * 0.17f, fs * 0.48f, TEXT, Paint.Align.CENTER);
            }
            line.setColor(BORDER); line.setStrokeWidth(2 * u);
            c.drawRoundRect(face, 10 * u, 10 * u, line);

            float x = r.left + 16 * u + fs + 14 * u, xr = r.right - 16 * u;
            boolean three = battle;
            float lineH = (ch - 24 * u) / (three ? 4f : 3f);
            float y = r.top + 12 * u + lineH * 0.8f;
            text.setTypeface(sans);
            label(c, ellipsize(name, (xr - x) * 0.7f, 28 * k * u), x, y, 28 * k * u, TEXT, Paint.Align.LEFT);
            label(c, "Lv " + m.optInt("lv"), xr, y, 22 * k * u, LABEL, Paint.Align.RIGHT);
            long hp = m.optLong("hp"), mhp = Math.max(1, m.optLong("mhp"));
            y += lineH;
            statBar(c, "HP", x, y, xr, hp / (double) mhp, hp * 4 < mhp ? LOW : HP, String.valueOf(hp), k, u);
            if (battle) {
                double atb = m.optDouble("atb", 0) / Math.max(1, m.optDouble("matb", 1));
                y += lineH;
                statBar(c, "ATB", x, y, xr, atb, ATB, atb >= 1 ? "READY" : "", k, u);
                int tr = m.optInt("trance", -1);
                if (tr >= 0) { y += lineH; statBar(c, "TRN", x, y, xr, tr / 255.0, TRANCE, tr >= 255 ? "!!" : "", k, u); }
            } else {
                y += lineH;
                statBar(c, "MP", x, y, xr, m.optLong("mp") / (double) Math.max(1, m.optLong("mmp")), MP, String.valueOf(m.optLong("mp")), k, u);
            }
        }
    }

    private void statBar(Canvas c, String name, float x, float y, float xr, double f, int[] col, String value, float k, float u) {
        text.setTypeface(sans);
        label(c, name, x, y, 20 * k * u, LABEL, Paint.Align.LEFT);
        float bx = x + 52 * k * u, bxr = xr - 96 * k * u;
        bar(c, bx, y - 11 * k * u, bxr - bx, 12 * u, f, col);
        label(c, value, xr, y, 22 * k * u, "READY".equals(value) ? GOLD : TEXT, Paint.Align.RIGHT);
    }

    // ---- panels

    private void drawEnemies(Canvas c, RectF in, float u) {
        JSONArray en = state.optJSONArray("enemies");
        List<JSONObject> list = new ArrayList<>();
        if (en != null) for (int i = 0; i < en.length(); i++) { JSONObject e = en.optJSONObject(i); if (e != null && e.optInt("target") == 1) list.add(e); }
        if (list.isEmpty()) { empty(c, in, "No enemies in sight", u); return; }
        boolean full = list.size() == 1;
        float y = in.top - scroll, start = y;
        for (JSONObject e : list) {
            long hp = e.optLong("hp"), mhp = Math.max(1, e.optLong("mhp"));
            text.setTypeface(sansBold);
            String name = clean(e.optString("name"));
            label(c, ellipsize(name, in.width() * 0.5f, 30 * u), in.left, y + 34 * u, 30 * u, hp > 0 ? TEXT : LABEL, Paint.Align.LEFT);
            text.setTextSize(30 * u);
            float nw = Math.min(text.measureText(name), in.width() * 0.5f);
            text.setTypeface(sans);
            if (e.optInt("lv") > 0) label(c, "Lv " + e.optInt("lv"), in.left + nw + 14 * u, y + 34 * u, 26 * u, LABEL, Paint.Align.LEFT);
            text.setTypeface(sansBold);
            label(c, String.format(Locale.US, "%,d / %,d", hp, mhp), in.right, y + 34 * u, 28 * u, TEXT, Paint.Align.RIGHT);
            y += 50 * u;
            bar(c, in.left, y, in.width(), 16 * u, hp / (double) mhp, hp * 4 < mhp ? LOW : HP);
            y += 30 * u;
            if (full) {
                y = chipRow(c, in, y, "Weak", e.optJSONArray("weak"), 0, u);
                y = chipRow(c, in, y, "Absorbs", e.optJSONArray("absorb"), 1, u);
                y = chipRow(c, in, y, "Immune", e.optJSONArray("immune"), 2, u);
                y = chipRow(c, in, y, "Halves", e.optJSONArray("half"), 3, u);
                y = textRow(c, in, y, "Left to steal", join(e.optJSONArray("steal"), "nothing left"), GOLD, u);
                y = textRow(c, in, y, "Drops", join(e.optJSONArray("drop"), "-"), TEXT, u);
            } else {
                // compact: one line of chips (weak, absorbs, immune), one of steal and drops
                float x = in.left;
                x = chips(c, x, y, in.right, e.optJSONArray("weak"), 0, "Weak", u);
                x = chips(c, x, y, in.right, e.optJSONArray("absorb"), 1, "Absorbs", u);
                chips(c, x, y, in.right, e.optJSONArray("immune"), 2, "Immune", u);
                y += 46 * u;
                text.setTypeface(sans);
                String steal = join(e.optJSONArray("steal"), "nothing left");
                label(c, "Steal", in.left, y + 4 * u, 24 * u, LABEL, Paint.Align.LEFT);
                label(c, ellipsize(steal, in.width() - 90 * u, 24 * u), in.left + 80 * u, y + 4 * u, 24 * u, GOLD, Paint.Align.LEFT);
                y += 34 * u;
                label(c, "Drops", in.left, y + 4 * u, 24 * u, LABEL, Paint.Align.LEFT);
                label(c, ellipsize(join(e.optJSONArray("drop"), "-"), in.width() - 90 * u, 24 * u), in.left + 80 * u, y + 4 * u, 24 * u, TEXT, Paint.Align.LEFT);
                y += 22 * u;
            }
            y += 18 * u;
            if (e != list.get(list.size() - 1)) {   // a divider between enemies
                line.setColor(BORDER); line.setStrokeWidth(2 * u);
                c.drawLine(in.left, y - 8 * u, in.right, y - 8 * u, line);
            }
            y += 6 * u;
        }
        contentH = y - start;
    }

    private float chipRow(Canvas c, RectF in, float y, String name, JSONArray list, int kind, float u) {
        rowLine(c, in, y + 52 * u, u);
        text.setTypeface(sans);
        label(c, name, in.left, y + 34 * u, 28 * u, LABEL, Paint.Align.LEFT);
        if (list == null || list.length() == 0) { label(c, "-", in.right, y + 34 * u, 28 * u, TEXT, Paint.Align.RIGHT); return y + 54 * u; }
        float x = in.right;
        for (int i = list.length() - 1; i >= 0; i--) x = chip(c, x, y + 8 * u, list.optString(i), kind, true, u) - 8 * u;
        return y + 54 * u;
    }

    private float textRow(Canvas c, RectF in, float y, String name, String value, int color, float u) {
        rowLine(c, in, y + 52 * u, u);
        text.setTypeface(sans);
        label(c, name, in.left, y + 34 * u, 28 * u, LABEL, Paint.Align.LEFT);
        text.setTextSize(28 * u);
        float lw = text.measureText(name) + 30 * u;
        label(c, ellipsize(value, in.width() - lw, 28 * u), in.right, y + 34 * u, 28 * u, color, Paint.Align.RIGHT);
        return y + 54 * u;
    }

    private float chips(Canvas c, float x, float y, float max, JSONArray list, int kind, String what, float u) {
        if (list == null || list.length() == 0) return x;
        text.setTypeface(sans);
        label(c, what, x, y + 26 * u, 22 * u, LABEL, Paint.Align.LEFT);
        text.setTextSize(22 * u);
        x += text.measureText(what) + 8 * u;
        for (int i = 0; i < list.length() && x < max - 60 * u; i++) x = chip(c, x, y, list.optString(i), kind, false, u) + 6 * u;
        return x + 14 * u;
    }

    /** An element as a coloured chip; anchored at its right edge (alignRight) or its left. Returns the other edge. */
    private float chip(Canvas c, float x, float y, String s, int kind, boolean alignRight, float u) {
        text.setTypeface(sans);
        text.setTextSize(24 * u);
        float cw = text.measureText(s) + 28 * u, chh = 36 * u;
        RectF r = alignRight ? new RectF(x - cw, y, x, y + chh) : new RectF(x, y, x + cw, y + chh);
        int[] col = CHIP[kind];
        fill.setShader(null);
        fill.setColor(col[0]);
        c.drawRoundRect(r, chh / 2, chh / 2, fill);
        line.setColor(col[2]); line.setStrokeWidth(1.5f * u);
        c.drawRoundRect(r, chh / 2, chh / 2, line);
        label(c, s, r.centerX(), r.centerY() + 8.5f * u, 24 * u, col[1], Paint.Align.CENTER);
        return alignRight ? r.left : r.right;
    }

    private void drawAbilities(Canvas c, RectF in, float u) {
        JSONArray party = state.optJSONArray("party");
        float y = in.top - scroll, start = y;
        boolean any = false;
        if (party != null) for (int i = 0; i < party.length(); i++) {
            JSONObject m = party.optJSONObject(i);
            if (m == null) continue;
            JSONArray learn = m.optJSONArray("learn");
            text.setTypeface(sansBold);
            String name = clean(m.optString("name"));
            label(c, name, in.left, y + 34 * u, 30 * u, TEXT, Paint.Align.LEFT);
            text.setTextSize(30 * u);
            float nw = text.measureText(name) + 20 * u;
            text.setTypeface(sans);
            label(c, ellipsize(m.optString("gear"), in.width() - nw, 22 * u), in.right, y + 32 * u, 22 * u, LABEL, Paint.Align.RIGHT);
            y += 48 * u;
            if (learn == null || learn.length() == 0) {
                label(c, "The equipment teaches nothing new", in.left, y + 26 * u, 24 * u, LABEL, Paint.Align.LEFT);
                y += 44 * u;
            } else for (int j = 0; j < learn.length(); j++) {
                JSONObject a = learn.optJSONObject(j);
                if (a == null) continue;
                any = true;
                int ap = a.optInt("ap"), max = Math.max(1, a.optInt("max"));
                boolean done = a.optInt("done") == 1 || ap >= max;
                label(c, clean(a.optString("n")), in.left, y + 28 * u, 28 * u, TEXT, Paint.Align.LEFT);
                label(c, done ? "learnt" : ap + " / " + max + " AP", in.right, y + 28 * u, 28 * u, done ? OK : TEXT, Paint.Align.RIGHT);
                bar(c, in.left, y + 40 * u, in.width(), 10 * u, done ? 1 : ap / (double) max, done ? LEARNT : MP);
                y += 62 * u;
            }
            y += 10 * u;
        }
        if (!any && party == null) empty(c, in, "Nothing here yet", u);
        contentH = y - start;
    }

    private void drawItems(Canvas c, RectF in, float u) {
        JSONArray items = state.optJSONArray("items");
        if (items == null || items.length() == 0) { empty(c, in, "No items", u); return; }
        int cols = narrow() ? 1 : 2;
        float colGap = 24 * u, cw = (in.width() - colGap * (cols - 1)) / cols, rh = 52 * u;
        float start = in.top - scroll;
        for (int i = 0; i < items.length(); i++) {
            JSONObject it = items.optJSONObject(i);
            if (it == null) continue;
            float x = in.left + (i % cols) * (cw + colGap), y = start + (i / cols) * rh;
            if (y + rh < in.top - 4 * u || y > in.bottom + 4 * u) continue;
            Bitmap ic = image(it.optString("icon"));
            float is = 36 * u;
            if (ic != null) c.drawBitmap(ic, null, new RectF(x, y + (rh - is) / 2, x + is, y + (rh + is) / 2), bmpPaint);
            text.setTypeface(sans);
            String n = String.valueOf(it.optInt("c"));
            text.setTextSize(26 * u);
            float nw = text.measureText(n) + 16 * u;
            label(c, ellipsize(clean(it.optString("n")), cw - is - 14 * u - nw, 26 * u), x + is + 12 * u, y + rh / 2 + 9 * u, 26 * u, TEXT, Paint.Align.LEFT);
            label(c, n, x + cw, y + rh / 2 + 9 * u, 26 * u, LABEL, Paint.Align.RIGHT);
            if (i % cols == cols - 1 || i == items.length() - 1) rowLine(c, in, y + rh, u);
        }
        contentH = ((items.length() + cols - 1) / cols) * rh;
    }

    private void drawKeys(Canvas c, RectF in, float u) {
        JSONArray keys = state.optJSONArray("keys");
        if (keys == null || keys.length() == 0) { empty(c, in, "No key items", u); return; }
        float rh = 50 * u, start = in.top - scroll;
        for (int i = 0; i < keys.length(); i++) {
            JSONObject k = keys.optJSONObject(i);
            if (k == null) continue;
            float y = start + i * rh;
            if (y + rh < in.top - 4 * u || y > in.bottom + 4 * u) continue;
            boolean used = k.optInt("used") == 1;
            text.setTypeface(sans);
            label(c, ellipsize(clean(k.optString("n")), in.width() - 120 * u, 27 * u), in.left, y + rh / 2 + 9 * u, 27 * u, used ? LABEL : TEXT, Paint.Align.LEFT);
            if (used) label(c, "used", in.right, y + rh / 2 + 9 * u, 24 * u, LABEL, Paint.Align.RIGHT);
            rowLine(c, in, y + rh, u);
        }
        contentH = keys.length() * rh;
    }

    private void drawExtras(Canvas c, RectF in, float u) {
        float y = in.top;
        // Excalibur II: the game's own rule is reaching the last save point in under 12 hours
        long left = 12 * 3600 - state.optLong("time");
        RectF banner = new RectF(in.left, y, in.right, y + 60 * u);
        fill.setShader(new LinearGradient(banner.left, 0, banner.right, 0, 0xFF4A3408, 0xFF2A1E05, Shader.TileMode.CLAMP));
        c.drawRoundRect(banner, 10 * u, 10 * u, fill);
        fill.setShader(null);
        line.setColor(0xFFD6B050); line.setStrokeWidth(2 * u);
        c.drawRoundRect(banner, 10 * u, 10 * u, line);
        text.setTypeface(sans);
        label(c, "Excalibur II (12 h)", banner.left + 18 * u, banner.centerY() + 10 * u, 28 * u, GOLD, Paint.Align.LEFT);
        label(c, left > 0 ? String.format(Locale.US, "%d:%02d:%02d left", left / 3600, left / 60 % 60, left % 60) : "time is up",
                banner.right - 18 * u, banner.centerY() + 10 * u, 28 * u, GOLD, Paint.Align.RIGHT);
        y = banner.bottom + 14 * u;

        // the four boosters
        String[] names = { "High speed", "No battles", "Battle assist", "9999 damage" };
        int[] cmds = { CMD_SPEED, CMD_BATTLES, CMD_BPLUS, CMD_DMG };
        int bcols = narrow() ? 1 : 2;
        float bw = (in.width() - 12 * u * (bcols - 1)) / bcols, bh = 70 * u;
        for (int i = 0; i < 4; i++) {
            float x = in.left + (i % bcols) * (bw + 12 * u), by = y + (i / bcols) * (bh + 12 * u);
            RectF r = new RectF(x, by, x + bw, by + bh);
            boolean on = isOn(cmds[i]);
            boolean hot = pressed != null && pressed.kind == H_CMD && pressed.arg == cmds[i] && pressed.box.equals(r);
            button(c, r, on, hot, u);
            text.setTypeface(sans);
            String l = names[i];
            text.setTextSize(27 * u);
            float lw = text.measureText(l), sw = 60 * u;
            float lx = r.centerX() - (lw + sw) / 2;
            label(c, l, lx, r.centerY() + 10 * u, 27 * u, on ? GOLD : TEXT, Paint.Align.LEFT);
            text.setTypeface(sansBold);
            label(c, on ? "ON" : "OFF", lx + lw + 14 * u, r.centerY() + 9 * u, 22 * u, on ? GOLD : TEXT, Paint.Align.LEFT);
            hits.add(new Hit(r, H_CMD, cmds[i]));
        }
        y += (4 / bcols) * (bh + 12 * u) + 4 * u;

        // the counters
        JSONObject k = state.optJSONObject("counters");
        if (k == null) return;
        String[][] rows = {
            { "Successful steals", k.has("steals") ? String.valueOf(k.optInt("steals")) : "-" },
            { "Frogs caught", k.has("frogs") ? String.valueOf(k.optInt("frogs")) : "-" },
            { "Dragons slain", k.has("dragons") ? String.valueOf(k.optInt("dragons")) : "-" },
            { "Treasure hunter points", k.has("treasure") ? String.valueOf(k.optInt("treasure")) : "-" },
            { "Chocographs", k.has("chocographs") ? k.optInt("chocographs") + " / 24" : "-" },
            { "Cards (won / lost / draw)", k.has("cardW") ? k.optInt("cardW") + " / " + k.optInt("cardL") + " / " + k.optInt("cardD") : "-" },
        };
        float rh = Math.min(52 * u, (in.bottom - y) / rows.length);
        for (int i = 0; i < rows.length; i++) {
            float ry = y + i * rh;
            text.setTypeface(sans);
            float fs = Math.min(28 * u, rh * 0.6f);   // narrow: less room, smaller rows
            label(c, rows[i][0], in.left, ry + rh / 2 + fs * 0.36f, fs, LABEL, Paint.Align.LEFT);
            label(c, rows[i][1], in.right, ry + rh / 2 + fs * 0.36f, fs, TEXT, Paint.Align.RIGHT);
            if (i < rows.length - 1) rowLine(c, in, ry + rh, u);
        }
    }

    /** The world map (when the game has saved one) with the party's arrow, the chocobo and the airship; in a field the place's name over it. */
    private void drawMap(Canvas c, RectF in, boolean world, String loc, float u) {
        String name = state.optString("wmap", "world_map_full_all");
        Bitmap map = image(name);
        if (map != null) {
            float scale = Math.min(in.width() / map.getWidth(), in.height() / map.getHeight());
            float mw = map.getWidth() * scale, mh = map.getHeight() * scale;
            RectF dst = new RectF(in.centerX() - mw / 2, in.centerY() - mh / 2, in.centerX() + mw / 2, in.centerY() + mh / 2);
            bmpPaint.setAlpha(world ? 255 : 110);
            c.drawBitmap(map, null, dst, bmpPaint);
            bmpPaint.setAlpha(255);
            if (world) {
                if (state.has("chx")) marker(c, dst, state.optDouble("chx"), state.optDouble("chy"), 0xFFF3D27A, "C", u);
                if (state.has("asx")) marker(c, dst, state.optDouble("asx"), state.optDouble("asy"), 0xFFC8D4F0, "A", u);
                if (state.has("wx")) {
                    float px = dst.left + dst.width() * (float) state.optDouble("wx"), py = dst.top + dst.height() * (1f - (float) state.optDouble("wy"));
                    fill.setShader(null);
                    fill.setColor(0x66E05050);
                    c.drawCircle(px, py, 26 * u, fill);
                    c.save();
                    c.translate(px, py);
                    c.rotate((float) state.optDouble("wrot") + 90f);
                    Path arrow = new Path();
                    arrow.moveTo(20 * u, 0); arrow.lineTo(-12 * u, -12 * u); arrow.lineTo(-6 * u, 0); arrow.lineTo(-12 * u, 12 * u); arrow.close();
                    fill.setColor(0xFFFFFFFF);
                    c.drawPath(arrow, fill);
                    line.setColor(0xFF201810); line.setStrokeWidth(2.5f * u);
                    c.drawPath(arrow, line);
                    c.restore();
                }
                // the legend
                boolean ch = state.has("chx"), as = state.has("asx");
                if (ch || as) {
                    String l = "▲ You" + (ch ? "   ● Chocobo" : "") + (as ? "   ● Airship" : "");
                    text.setTypeface(sans);
                    text.setTextSize(22 * u);
                    float lw = text.measureText(l) + 24 * u;
                    RectF lr = new RectF(dst.left + 10 * u, dst.bottom - 46 * u, dst.left + 10 * u + lw, dst.bottom - 10 * u);
                    fill.setShader(null);
                    fill.setColor(0xCC0A1230);
                    c.drawRoundRect(lr, 8 * u, 8 * u, fill);
                    label(c, l, lr.left + 12 * u, lr.centerY() + 8 * u, 22 * u, TEXT, Paint.Align.LEFT);
                }
            }
        }
        if (!world) {
            text.setTypeface(sansBold);
            String l = loc.isEmpty() ? " " : loc;
            label(c, l, in.centerX(), in.centerY() + 16 * u, fit(l, in.width() - 40 * u, 44 * u), TEXT, Paint.Align.CENTER);
        }
    }

    private void marker(Canvas c, RectF dst, double x, double y, int color, String letter, float u) {
        float px = dst.left + dst.width() * (float) x, py = dst.top + dst.height() * (1f - (float) y);
        fill.setShader(null);
        fill.setColor(color);
        c.drawCircle(px, py, 15 * u, fill);
        line.setColor(0xFF201810); line.setStrokeWidth(2.5f * u);
        c.drawCircle(px, py, 15 * u, line);
        text.setTypeface(sansBold);
        label(c, letter, px, py + 7 * u, 20 * u, 0xFF201810, Paint.Align.CENTER);
    }

    private void empty(Canvas c, RectF in, String s, float u) {
        text.setTypeface(sans);
        label(c, s, in.centerX(), in.centerY(), 28 * u, LABEL, Paint.Align.CENTER);
    }

    private static String join(JSONArray a, String none) {
        if (a == null || a.length() == 0) return none;
        StringBuilder b = new StringBuilder();
        for (int i = 0; i < a.length(); i++) { if (i > 0) b.append(" · "); b.append(clean(a.optString(i))); }
        return b.toString();
    }

    // ---- pieces

    // the game's window skin, when the game has saved it (Core/Companion.cs: skinBg, skinFrame)
    private Bitmap skinBgOf;
    private android.graphics.BitmapShader skinShader;
    private final android.graphics.Rect src = new android.graphics.Rect();


    /** A window as in the game's menus: its paper texture tiled inside its silver frame (nine-slice:
     *  corners as they are, edges stretched). Without the skin: FF9's stone gradient and cream frame. */
    private void window(Canvas c, RectF r, float u) {
        Bitmap bg = image(state.optString("skinBg")), fr = image(state.optString("skinFrame"));
        if (bg == null || fr == null) {
            float rad = 12 * u;
            fill.setShader(new LinearGradient(0, r.top, 0, r.bottom, new int[] { WIN_A, WIN_B, WIN_C }, new float[] { 0f, 0.5f, 1f }, Shader.TileMode.CLAMP));
            c.drawRoundRect(r, rad, rad, fill);
            fill.setShader(null);
            line.setColor(BORDER);
            line.setStrokeWidth(3 * u);
            c.drawRoundRect(r, rad, rad, line);
            line.setColor(INNER);
            line.setStrokeWidth(2 * u);
            c.drawRoundRect(new RectF(r.left + 3.5f * u, r.top + 3.5f * u, r.right - 3.5f * u, r.bottom - 3.5f * u), rad - 3 * u, rad - 3 * u, line);
            return;
        }
        if (skinBgOf != bg) {
            skinBgOf = bg;
            skinShader = new android.graphics.BitmapShader(bg, Shader.TileMode.REPEAT, Shader.TileMode.REPEAT);
        }
        android.graphics.Matrix m = new android.graphics.Matrix();
        m.setScale(u * 1.4f, u * 1.4f);
        m.postTranslate(r.left, r.top);
        skinShader.setLocalMatrix(m);
        fill.setShader(skinShader);
        float in = 6 * u;
        c.drawRoundRect(r.left + in, r.top + in, r.right - in, r.bottom - in, 8 * u, 8 * u, fill);
        fill.setShader(null);
        int w = fr.getWidth(), h = fr.getHeight(), k = Math.min(w, h) * 2 / 5;
        float d = Math.min(26 * u, Math.min(r.width(), r.height()) / 2f);
        float[] dx = { r.left, r.left + d, r.right - d, r.right }, dy = { r.top, r.top + d, r.bottom - d, r.bottom };
        int[] sx = { 0, k, w - k, w }, sy = { 0, k, h - k, h };
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) {
            if (i == 1 && j == 1) continue;
            src.set(sx[i], sy[j], sx[i + 1], sy[j + 1]);
            c.drawBitmap(fr, src, new RectF(dx[i], dy[j], dx[i + 1], dy[j + 1]), bmpPaint);
        }
    }

    /** The hide button: a small window with a chevron pointing to the panels' outer edge. */
    private void drawHideButton(Canvas c, RectF b, boolean hot, float u) {
        window(c, b, u);
        if (hot) { fill.setShader(null); fill.setColor(0x55FFFFFF); c.drawRoundRect(b, 10 * u, 10 * u, fill); }
        line.setColor(TEXT);
        line.setStrokeWidth(5 * u);
        line.setStrokeCap(Paint.Cap.ROUND);
        float cx = b.centerX(), cy = b.centerY(), dx = b.width() * 0.13f, dy = b.height() * 0.22f;
        // panels at the game's right hide to the right: the chevron points away from the game
        float tip = hideAtLeft ? cx + dx : cx - dx, back = hideAtLeft ? cx - dx : cx + dx;
        c.drawLine(back, cy - dy, tip, cy, line);
        c.drawLine(tip, cy, back, cy + dy, line);
        line.setStrokeCap(Paint.Cap.BUTT);
    }

    private void tabShape(Canvas c, RectF r, boolean sel, boolean hot, float u) {
        float rad = 10 * u;
        if (sel) {
            fill.setShader(new LinearGradient(0, r.top, 0, r.bottom, GOLD_A, GOLD_B, Shader.TileMode.CLAMP));
            c.drawRoundRect(r, rad, rad, fill);
            fill.setShader(null);
            line.setColor(GOLD_EDGE);
            line.setStrokeWidth(2 * u);
            c.drawRoundRect(r, rad, rad, line);
            return;
        }
        window(c, r, u);
        if (hot) { fill.setShader(null); fill.setColor(0x40F0CF72); c.drawRoundRect(r, rad, rad, fill); }
    }

    private void button(Canvas c, RectF r, boolean on, boolean hot, float u) {
        float rad = 12 * u;
        if (on) {   // a soft gold glow around a lit button
            fill.setShader(null);
            fill.setColor(0x40C99A3A);
            c.drawRoundRect(new RectF(r.left - 5 * u, r.top - 5 * u, r.right + 5 * u, r.bottom + 5 * u), rad + 5 * u, rad + 5 * u, fill);
        }
        window(c, r, u);
        fill.setShader(null);
        if (hot) { fill.setColor(0x40F0CF72); c.drawRoundRect(r, rad, rad, fill); }
        if (on) {
            line.setColor(GOLD_EDGE);
            line.setStrokeWidth(3 * u);
            c.drawRoundRect(r, rad, rad, line);
        }
    }

    /** Small drawn icons for the deck (no emoji: they differ between devices). */
    private void deckIcon(Canvas c, int cmd, float x, float y, int color, float u) {
        fill.setShader(null);
        fill.setColor(color);
        line.setColor(color);
        line.setStrokeWidth(3.5f * u);
        line.setStrokeCap(Paint.Cap.ROUND);
        float s = 14 * u;
        Path p = new Path();
        switch (cmd) {
            case CMD_MENU:
                for (int i = -1; i <= 1; i++) c.drawLine(x - s, y + i * s * 0.7f, x + s, y + i * s * 0.7f, line);
                break;
            case CMD_SAVE:
                c.drawRoundRect(x - s, y - s, x + s, y + s, 3 * u, 3 * u, line);
                c.drawRect(x - s * 0.5f, y - s, x + s * 0.5f, y - s * 0.3f, fill);
                c.drawRect(x - s * 0.6f, y + s * 0.2f, x + s * 0.6f, y + s * 0.8f, fill);
                break;
            case CMD_LOAD:
                p.moveTo(x - s, y - s * 0.8f); p.lineTo(x - s * 0.2f, y - s * 0.8f); p.lineTo(x, y - s * 0.4f); p.lineTo(x + s, y - s * 0.4f);
                p.lineTo(x + s, y + s * 0.9f); p.lineTo(x - s, y + s * 0.9f); p.close();
                c.drawPath(p, line);
                break;
            case CMD_SPEED:
                p.moveTo(x - s, y - s * 0.8f); p.lineTo(x, y); p.lineTo(x - s, y + s * 0.8f); p.close();
                p.moveTo(x, y - s * 0.8f); p.lineTo(x + s, y); p.lineTo(x, y + s * 0.8f); p.close();
                c.drawPath(p, fill);
                break;
            case CMD_BATTLES:
                p.moveTo(x, y - s); p.lineTo(x + s * 0.9f, y - s * 0.6f); p.lineTo(x + s * 0.8f, y + s * 0.2f);
                p.quadTo(x + s * 0.5f, y + s * 0.8f, x, y + s); p.quadTo(x - s * 0.5f, y + s * 0.8f, x - s * 0.8f, y + s * 0.2f);
                p.lineTo(x - s * 0.9f, y - s * 0.6f); p.close();
                c.drawPath(p, line);
                break;
            case CMD_BPLUS:
                c.drawLine(x - s, y, x + s, y, line);
                c.drawLine(x, y - s, x, y + s, line);
                break;
        }
        line.setStrokeCap(Paint.Cap.BUTT);
    }

    private void drawBook(Canvas c, RectF b, boolean hot, float u) {
        fill.setShader(null);
        fill.setColor(hot ? GOLD : 0xFFF4F0E4);
        float mid = b.centerX();
        Path left = new Path(), right = new Path();
        left.moveTo(mid, b.top + 6 * u); left.lineTo(b.left, b.top); left.lineTo(b.left, b.bottom - 4 * u); left.lineTo(mid, b.bottom); left.close();
        right.moveTo(mid, b.top + 6 * u); right.lineTo(b.right, b.top); right.lineTo(b.right, b.bottom - 4 * u); right.lineTo(mid, b.bottom); right.close();
        c.drawPath(left, fill);
        c.drawPath(right, fill);
        line.setColor(0xFF9AA0B4);
        line.setStrokeWidth(2.5f * u);
        for (int i = 1; i <= 3; i++) {
            float yy = b.top + b.height() * i / 4.5f;
            c.drawLine(b.left + 8 * u, yy, mid - 6 * u, yy + 3 * u, line);
            c.drawLine(mid + 6 * u, yy + 3 * u, b.right - 8 * u, yy, line);
        }
    }

    private void rowLine(Canvas c, RectF in, float y, float u) {
        line.setColor(ROW);
        line.setStrokeWidth(1.5f * u);
        c.drawLine(in.left, y, in.right, y, line);
    }

    private void bar(Canvas c, float x, float y, float bw, float bh, double f, int[] col) {
        f = Math.max(0, Math.min(1, f));
        fill.setShader(null);
        fill.setColor(TRACK);
        c.drawRoundRect(x, y, x + bw, y + bh, bh / 2, bh / 2, fill);
        if (f > 0) {
            float xe = x + (float) (bw * f);
            fill.setShader(new LinearGradient(x, 0, x + bw, 0, col[0], col[1], Shader.TileMode.CLAMP));
            c.drawRoundRect(x, y, Math.max(xe, x + bh), y + bh, bh / 2, bh / 2, fill);
            fill.setShader(null);
        }
    }

    private void label(Canvas c, String s, float x, float y, float size, int color, Paint.Align a) {
        text.setTextSize(size);
        text.setColor(color);
        text.setTextAlign(a);
        text.setShadowLayer(size * 0.06f, size * 0.05f, size * 0.05f, 0xC0000000);
        c.drawText(s, x, y, text);
    }

    private float fit(String s, float maxW, float size) {
        text.setTextSize(size);
        float tw = text.measureText(s);
        return tw <= maxW ? size : size * maxW / tw;
    }

    private String ellipsize(String s, float maxW, float size) {
        text.setTextSize(size);
        if (text.measureText(s) <= maxW) return s;
        while (s.length() > 1 && text.measureText(s + "…") > maxW) s = s.substring(0, s.length() - 1);
        return s + "…";
    }

    // ------------------------------------------------------------------ touch

    private Hit hitAt(float x, float y) {
        for (int i = hits.size() - 1; i >= 0; i--) if (hits.get(i).box.contains(x, y)) return hits.get(i);
        return null;
    }

    @Override
    public boolean onTouchEvent(MotionEvent e) {
        if (state.optInt("ready") != 1) { pressed = null; return true; }
        float x = e.getX(), y = e.getY();
        switch (e.getActionMasked()) {
            case MotionEvent.ACTION_DOWN: {
                downX = x; downY = y; downScroll = scroll; dragging = false;
                pressed = hitAt(x, y);
                if (pressed != null) performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY);
                invalidate();
                return true;
            }
            case MotionEvent.ACTION_MOVE: {
                if (!dragging && panelBox.contains(downX, downY) && Math.abs(y - downY) > 14 * getResources().getDisplayMetrics().density) {
                    dragging = true;
                    pressed = null;
                }
                if (dragging) { scroll = Math.max(0, downScroll - (y - downY)); invalidate(); return true; }
                Hit h = hitAt(x, y);
                if (h != pressed) { pressed = h != null && pressed != null && h.box.equals(pressed.box) ? h : null; invalidate(); }
                return true;
            }
            case MotionEvent.ACTION_UP: {
                Hit h = pressed;
                pressed = null;
                invalidate();
                if (!dragging && h != null && h.box.contains(x, y)) run(h);
                dragging = false;
                return true;
            }
            case MotionEvent.ACTION_CANCEL:
                pressed = null;
                dragging = false;
                invalidate();
                return true;
        }
        return true;
    }

    private void run(Hit h) {
        switch (h.kind) {
            case H_TAB:
                tab = h.arg;
                prefs.edit().putInt("deck_tab", tab).apply();
                return;
            case H_HIDE:
                partyHidden = !partyHidden;
                prefs.edit().putBoolean("deck_party_hidden", partyHidden).apply();
                return;
            case H_PANELS:
                if (onHide != null) onHide.run();
                return;
            case H_BOOK:
                if (GameActivity.hasGuide()) { GameActivity.toggleGuideFromPanel(); return; }   // the player's own page
                refreshGuide();          // installed or removed meanwhile
                if (guideIntent != null) openGuide();
                else GuideInstaller.start(getContext(), getDisplay() != null ? getDisplay().getDisplayId() : -1);
                return;
        }
        int cmd = h.arg;
        switch (cmd) {
            case CMD_MENU: GameActivity.tapFromPanel(GameActivity.Y); return;
            case CMD_SAVE: GameActivity.keepStateFromPanel(this::note); return;
            case CMD_LOAD: GameActivity.loadLatestFromPanel(this::note); return;
            case CMD_SPEED: note(isOn(cmd) ? "Normal speed" : "Speed " + speedFactor() + "×"); break;
            case CMD_BATTLES: note(isOn(cmd) ? "Random battles on" : "No random battles"); break;
            case CMD_BPLUS: note(isOn(cmd) ? "Battle assistance off" : "Battle assistance on"); break;
            case CMD_DMG: note(isOn(cmd) ? "9999 damage off" : "9999 damage on"); break;
        }
        GameActivity.commandFromPanel(cmd);
    }

    // Pixel Guide (a game guide app, as in FF7Recomp): the book opens it here on the bottom
    // screen; when it is not installed the book offers to install it (GuideInstaller).
    static final String GUIDE_PACKAGE = "app.nanostack.pixelguide";
    private android.content.Intent guideIntent;    // null: not installed
    private long guideCheckedAt = -100000;

    private void refreshGuide() {
        guideCheckedAt = SystemClock.uptimeMillis();
        try { guideIntent = getContext().getPackageManager().getLaunchIntentForPackage(GUIDE_PACKAGE); }
        catch (RuntimeException e) { guideIntent = null; }
    }

    /** Started straight from here with no activity of ours on this screen: the Thor gives an app
     *  with an activity on the bottom screen that screen's volume (0 out of the box), which would
     *  mute the game. A guide already running comes back to the front where it was left. */
    private void openGuide() {
        try {
            android.content.Intent i = new android.content.Intent(guideIntent);
            i.addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK | android.content.Intent.FLAG_ACTIVITY_NO_ANIMATION);
            android.app.ActivityOptions o = android.app.ActivityOptions.makeBasic();
            if (getDisplay() != null) o.setLaunchDisplayId(getDisplay().getDisplayId());
            getContext().startActivity(i, o.toBundle());
            GameActivity.guideOpened();
        } catch (RuntimeException e) {
            note("Pixel Guide could not open");
        }
    }

    void note(String s) {
        flash = s;
        flashUntil = SystemClock.uptimeMillis() + 1800;
        invalidate();
    }
}
