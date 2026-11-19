package com.ff9recomp;

import android.app.Activity;
import android.os.Bundle;
import android.system.Os;
import android.util.Log;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.WindowManager;
import java.io.File;

/**
 * The game: a SurfaceView for the EGL window, the engine on its own thread (libff9.so), and the
 * controller as an XInput-style pad (the game reads XInput).
 */
public class GameActivity extends Activity implements SurfaceHolder.Callback {
    private static final String TAG = "FF9Recomp";
    private static boolean started;

    static native void nativeSetSurface(Object surface);
    static native int nativeRun(String dir, String log);
    static native String nativeCompanion();
    static native int nativeRumble();
    static native void nativeCommand(int cmd);
    static native int nativeFrames();
    static native void nativePad(int buttons, int lt, int rt, int lx, int ly, int rx, int ry);

    // XInput button bits
    static final int UP = 0x1, DOWN = 0x2, LEFT = 0x4, RIGHT = 0x8, START = 0x10, BACK = 0x20, LTHUMB = 0x40, RTHUMB = 0x80,
            LB = 0x100, RB = 0x200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
    private int buttons, lt, rt, lx, ly, rx, ry, dpad;

    private final android.os.Handler ui = new android.os.Handler(android.os.Looper.getMainLooper());
    private android.widget.FrameLayout root;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        System.loadLibrary("ff9");      // before the panels and the poller call into it (the surface may come much later)
        current = this;
        Reports.installCrashHandler(this);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        android.content.SharedPreferences prefs = getSharedPreferences("launcher", MODE_PRIVATE);
        root = new GameFrame(this);
        SurfaceView v = new SurfaceView(this);
        v.getHolder().addCallback(this);
        // The surface's buffer, from the size the view is laid out at:
        // - Settings > Resolution: a smaller buffer, scaled up by the display hardware (no cost)
        // - Settings > Picture > Stretch everything (GitHub issue 4, as FF7Recomp's): on a screen
        //   wider than 16:9 the game draws 16:9 and the display stretches it sideways
        int res = prefs.getInt("resolution", 0);
        int stretch = prefs.getInt("stretch", 0);
        float f = res == 1 ? 0.75f : res == 2 ? 0.5f : res == 3 ? 0.25f : 1f;
        v.addOnLayoutChangeListener((x, l, t, r, bt, ol, ot, or, ob) -> {
            int w = r - l, h = bt - t;
            if (w <= 0 || h <= 0) return;
            int bw = stretch == 2 && w * 9 > h * 16 + 9 ? Math.round(h * 16f / 9f) : w;
            if (f == 1f && bw == w) v.getHolder().setSizeFromLayout();
            else v.getHolder().setFixedSize(Math.max(2, Math.round(bw * f) & ~1), Math.max(2, Math.round(h * f) & ~1));
        });
        root.addView(v);
        // Settings > Portrait (GitHub issues 2 and 8): the game on top, the panels and pad under it
        portrait = prefs.getBoolean("portrait", false);
        if (portrait) {
            setRequestedOrientation(android.content.pm.ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT);
            panel = new CompanionView(this);
        }
        // the on-screen pad (Settings > Touch controls: Auto shows it when no controller is connected)
        touchMode = prefs.getInt("touch_controls", 0);
        if (touchMode != 2) {
            touch = new TouchControls(this, (tb, tl, tr, trx, try_) -> { touchButtons = tb; touchLt = tl; touchRt = tr; touchRx = trx; touchRy = try_; send(); });
            touch.setExtra(prefs.getBoolean("touch_extra", false));   // right stick, L3, R3 (as FF7Recomp's GitHub #62)
            touch.setLayout(prefs.getString("touch_layout", ""));
            root.addView(touch, new android.widget.FrameLayout.LayoutParams(-1, -1));
            ((android.hardware.input.InputManager) getSystemService(INPUT_SERVICE)).registerInputDeviceListener(deviceListener, null);
        }
        if (prefs.getBoolean("crt_filter", false)) root.addView(new CrtOverlay(this), new android.widget.FrameLayout.LayoutParams(-1, -1));
        if (prefs.getBoolean("show_fps", false)) {
            fpsView = new android.widget.TextView(this);
            fpsView.setTextColor(android.graphics.Color.YELLOW);
            fpsView.setTextSize(14);
            fpsView.setShadowLayer(3, 1, 1, android.graphics.Color.BLACK);
            fpsView.setPadding(16, 8, 16, 8);
            root.addView(fpsView, new android.widget.FrameLayout.LayoutParams(-2, -2, android.view.Gravity.TOP | android.view.Gravity.END));
        }
        rumble = prefs.getInt("rumble", 1);
        crt = prefs.getBoolean("crt_filter", false);
        arrange(portrait && isTall());
        // the whole screen: draw behind the (hidden) system bars and into display cutouts, so devices
        // with a navigation bar don't keep its strip empty; anything uncovered is black, not the theme's grey
        getWindow().setDecorFitsSystemWindows(false);
        WindowManager.LayoutParams cut = getWindow().getAttributes();
        cut.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS;
        getWindow().setAttributes(cut);
        getWindow().setBackgroundDrawable(new android.graphics.drawable.ColorDrawable(android.graphics.Color.BLACK));
        // Smooth 120: ask the display for its fastest mode at this resolution (the Thor's 120 Hz)
        preferRefreshRate(prefs.getInt("fps", 1) == 2 ? 120 : 60);
        startDisplayWatch(prefs.getInt("fps", 1));
        if (prefs.getBoolean("bottom_screen", true) && !portrait) showCompanion();
        ui.post(poller);
        updateTouch();
        hideSystemUi();
        android.content.IntentFilter padFilter = new android.content.IntentFilter("com.ff9recomp.test.PAD");
        if (android.os.Build.VERSION.SDK_INT >= 33)
            registerReceiver(testPad, padFilter, android.Manifest.permission.DUMP, null, RECEIVER_EXPORTED);
        else
            registerReceiver(testPad, padFilter, android.Manifest.permission.DUMP, null);
    }

    // ---- Portrait (GitHub issues 2 and 8; FF7Recomp's layout). The window held upright: the game
    // across the top at 16:9; under it the panels the AYN Thor shows on its second screen
    // (CompanionView, with its command deck), then the on-screen pad. A controller in use hides
    // the pad and the panels take its room. Held sideways (or before the turn), the usual layout.
    private boolean portrait, arrangedTall;
    private CompanionView panel;

    /** The game's frame: the whole window, or (tall) the window's width at 16:9. */
    private static final class GameFrame extends android.widget.FrameLayout {
        boolean fill = true;
        GameFrame(android.content.Context c) { super(c); }
        @Override protected void onMeasure(int w, int h) {
            if (fill) { super.onMeasure(w, h); return; }
            int width = MeasureSpec.getSize(w);
            super.onMeasure(MeasureSpec.makeMeasureSpec(width, MeasureSpec.EXACTLY),
                    MeasureSpec.makeMeasureSpec(Math.round(width * 9f / 16f), MeasureSpec.EXACTLY));
        }
    }

    private boolean isTall() {
        return getResources().getConfiguration().orientation == android.content.res.Configuration.ORIENTATION_PORTRAIT;
    }

    private void arrange(boolean tall) {
        arrangedTall = tall;
        for (View x : new View[] { root, panel, touch })
            if (x != null && x.getParent() != null) ((android.view.ViewGroup) x.getParent()).removeView(x);
        ((GameFrame) root).fill = !tall;
        if (!tall) {
            if (touch != null) root.addView(touch, 1, new android.widget.FrameLayout.LayoutParams(-1, -1));   // over the game, under the CRT lines and FPS
            setContentView(root);
            return;
        }
        android.widget.LinearLayout col = new android.widget.LinearLayout(this);
        col.setOrientation(android.widget.LinearLayout.VERTICAL);
        col.setBackgroundColor(0xFF000000);
        col.addView(root, new android.widget.LinearLayout.LayoutParams(-1, -2));
        col.addView(panel, new android.widget.LinearLayout.LayoutParams(-1, 0, 2f));
        if (touch != null) col.addView(touch, new android.widget.LinearLayout.LayoutParams(-1, 0, 3f));
        setContentView(col);
        Log.i(TAG, "portrait: the game on top, the panels and pad under it");
    }

    // ---- the second screen (AYN Thor's bottom screen): the panels, in a Presentation that never
    // takes focus; keys and sticks that reach it go to the game
    private android.app.Presentation companion;
    private static volatile boolean companionOn;
    static volatile GameActivity current;
    Activity companionHost;      // the panels on the main display when the screens are swapped

    /** The game's latest panel state (JSON) for CompanionView, or null. */
    static String companionState() {
        try { return nativeCompanion(); } catch (UnsatisfiedLinkError e) { return null; }
    }

    /** The index in ds of the first display that isn't display notId, or -1. */
    static int otherScreen(android.view.Display[] ds, int notId) {
        int[] ids = new int[ds.length];
        for (int i = 0; i < ds.length; i++) ids[i] = ds[i].getDisplayId();
        return otherScreen(ids, notId);
    }

    static int otherScreen(int[] ids, int notId) {
        for (int i = 0; i < ids.length; i++) if (ids[i] != notId) return i;
        return -1;
    }

    private void showCompanion() {
        android.hardware.display.DisplayManager dm = (android.hardware.display.DisplayManager) getSystemService(DISPLAY_SERVICE);
        android.view.Display[] ds = dm.getDisplays(android.hardware.display.DisplayManager.DISPLAY_CATEGORY_PRESENTATION);
        if (ds.length == 0) { Log.i(TAG, "no second display"); return; }
        android.view.Display mine = getDisplay();
        // the Ayaneo Pocket DS lists its main screen as a presentation display too, first
        // (GitHub issue 31): the second screen is the first one that isn't the game's own
        int other = otherScreen(ds, mine != null ? mine.getDisplayId() : android.view.Display.DEFAULT_DISPLAY);
        android.view.Display d = ds[other >= 0 ? other : 0];
        panelsFor = mine != null ? mine.getDisplayId() : -1;
        // which screens there are and where the game went (Report a problem has display.txt):
        // the Ayaneo Pocket DS doesn't always put the game where it was asked (GitHub issue 31)
        StringBuilder s = new StringBuilder("screens:");
        for (android.view.Display x : dm.getDisplays()) {
            android.graphics.Point p = new android.graphics.Point();
            x.getRealSize(p);
            s.append(' ').append(x.getDisplayId()).append(" \"").append(x.getName()).append("\" ")
             .append(p.x).append('x').append(p.y).append(" flags ").append(x.getFlags());
        }
        s.append("; second screen ").append(d.getDisplayId()).append("; game on ").append(panelsFor)
         .append("; swap screens ").append(getSharedPreferences("launcher", MODE_PRIVATE).getBoolean("swap_screens", false) ? "on" : "off");
        displayLog(s.toString());
        if (mine != null && mine.getDisplayId() != android.view.Display.DEFAULT_DISPLAY) {
            // swapped: the game is on the second display, the panels go to the main one
            displayLog("screens swapped: panels on the main display");
            try {
                android.app.ActivityOptions o = android.app.ActivityOptions.makeBasic();
                o.setLaunchDisplayId(android.view.Display.DEFAULT_DISPLAY);
                startActivity(new android.content.Intent(this, CompanionHostActivity.class)
                        .addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK | android.content.Intent.FLAG_ACTIVITY_NO_ANIMATION), o.toBundle());
                companionOn = true;
                try { Os.setenv("FF9_COMPANION", "1", true); } catch (Exception ignored) { }
            } catch (RuntimeException e) { Log.w(TAG, "panels not shown on the main display: " + e); }
            return;
        }
        if (mine != null && mine.getDisplayId() == d.getDisplayId()) { displayLog("the game is on the second screen itself: no panels"); return; }
        displayLog("panels on display " + d.getDisplayId() + " \"" + d.getName() + "\"");
        companion = new android.app.Presentation(this, d) {
            @Override public boolean dispatchKeyEvent(KeyEvent e) { return GameActivity.this.dispatchKeyEvent(e); }
            @Override public boolean dispatchGenericMotionEvent(MotionEvent e) { return GameActivity.this.dispatchGenericMotionEvent(e); }
        };
        companion.getWindow().addFlags(WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE | WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        android.widget.FrameLayout panels = new android.widget.FrameLayout(companion.getContext());
        panels.addView(new CompanionView(companion.getContext()));
        // the guide (Settings > Guide page): a web page the player chose, on this screen, swapped
        // with the panels by Select+L1; scrolled by touch
        String guideUrl = getSharedPreferences("launcher", MODE_PRIVATE).getString("guide_url", "");
        if (!guideUrl.isEmpty()) {
            guide = new android.webkit.WebView(companion.getContext());
            guide.getSettings().setJavaScriptEnabled(true);
            guide.getSettings().setDomStorageEnabled(true);
            guide.setWebViewClient(new android.webkit.WebViewClient());
            guide.setFocusable(false);
            guide.setVisibility(View.GONE);
            panels.addView(guide, new android.widget.FrameLayout.LayoutParams(-1, -1));
            guidePending = guideUrl;
        }
        if (crt) panels.addView(new CrtOverlay(companion.getContext()), new android.widget.FrameLayout.LayoutParams(-1, -1));
        companion.setContentView(panels);
        try {
            companion.show();
            companionOn = true;
            try { Os.setenv("FF9_COMPANION", "1", true); } catch (Exception ignored) { }
        } catch (RuntimeException e) {
            Log.w(TAG, "panels not shown: " + e);
            companion = null;
        }
    }

    // ---- Pixel Guide on the bottom screen (as FF7Recomp): the book opens it there; the panels
    // step aside (a Presentation is drawn above activities) and a small back button over the
    // guide brings them back, the guide staying where it was left.

    /** Pixel Guide is opening on the bottom screen: the panels step aside, and the game is put
     *  back in front on the top screen so the pad keeps playing it while the guide stays in view. */
    static void guideOpened() {
        GameActivity a = current;
        if (a == null) return;
        a.runOnUiThread(() -> {
            if (a.companion != null) a.companion.hide();
            a.showGuideTab(true);
            a.ui.postDelayed(() -> {
                // Android gives keys to the display that last started an activity: starting the
                // game again on the top screen (singleTask: onNewIntent) makes it the focused one
                try {
                    android.app.ActivityOptions o = android.app.ActivityOptions.makeBasic();
                    if (a.getDisplay() != null) o.setLaunchDisplayId(a.getDisplay().getDisplayId());
                    a.startActivity(new android.content.Intent(a, GameActivity.class).putExtra("refocus", true)
                            .addFlags(android.content.Intent.FLAG_ACTIVITY_REORDER_TO_FRONT | android.content.Intent.FLAG_ACTIVITY_NO_ANIMATION), o.toBundle());
                } catch (RuntimeException e) { Log.w(TAG, "game not brought back in front: " + e); }
            }, 900);
        });
    }

    /** A focus hand-back from guideOpened: nothing to change, the game keeps running. */
    @Override
    protected void onNewIntent(android.content.Intent intent) {
        super.onNewIntent(intent);
        Log.i(TAG, "back in front" + (intent.getBooleanExtra("refocus", false) ? " (guide open)" : ""));
    }

    /** Android's install confirmation for Pixel Guide is on the bottom screen: the panels step aside. */
    static void panelsAside() {
        GameActivity a = current;
        if (a == null) return;
        a.runOnUiThread(() -> { if (a.companion != null) a.companion.hide(); });
    }

    /** Pixel Guide's install ended: the panels come back. */
    static void guideClosed() {
        GameActivity a = current;
        if (a == null) return;
        a.runOnUiThread(() -> { a.showGuideTab(false); a.showPanels(); });
    }

    private android.app.Presentation guideTab;

    private void showPanels() {
        if (companion == null || companion.isShowing()) return;
        companion.show();
    }

    private void showGuideTab(boolean on) {
        if (!on) { if (guideTab != null) guideTab.hide(); return; }
        if (companion == null) return;
        if (guideTab == null) {
            guideTab = new android.app.Presentation(this, companion.getDisplay()) {
                @Override public boolean dispatchKeyEvent(KeyEvent e) { return GameActivity.this.dispatchKeyEvent(e); }
                @Override public boolean dispatchGenericMotionEvent(MotionEvent e) { return GameActivity.this.dispatchGenericMotionEvent(e); }
            };
            float dp = getResources().getDisplayMetrics().density;
            // a back arrow, drawn (no text, so it covers as little of the guide as it can)
            View tab = new View(guideTab.getContext()) {
                final android.graphics.Paint pen = new android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG);
                final android.graphics.Path arrow = new android.graphics.Path();
                @Override protected void onDraw(android.graphics.Canvas c) {
                    float w = getWidth(), h = getHeight(), cx = w / 2, cy = h / 2, r = Math.min(w, h) * 0.24f;
                    pen.setColor(0xFFFFFFFF);
                    pen.setStyle(android.graphics.Paint.Style.STROKE);
                    pen.setStrokeWidth(3.2f * dp);
                    pen.setStrokeCap(android.graphics.Paint.Cap.ROUND);
                    pen.setStrokeJoin(android.graphics.Paint.Join.ROUND);
                    arrow.reset();
                    arrow.moveTo(cx + r, cy); arrow.lineTo(cx - r, cy);
                    arrow.moveTo(cx - r * 0.1f, cy - r * 0.9f); arrow.lineTo(cx - r, cy); arrow.lineTo(cx - r * 0.1f, cy + r * 0.9f);
                    c.drawPath(arrow, pen);
                }
            };
            tab.setContentDescription("Back to the panels");
            android.graphics.drawable.GradientDrawable bg = new android.graphics.drawable.GradientDrawable(
                    android.graphics.drawable.GradientDrawable.Orientation.TOP_BOTTOM, new int[] { 0xF06A7088, 0xF02A2D3A });
            bg.setCornerRadius(12 * dp);
            bg.setStroke((int) (2 * dp), 0xFFDCD6C4);
            tab.setBackground(bg);
            tab.setOnClickListener(v -> {
                v.performHapticFeedback(android.view.HapticFeedbackConstants.VIRTUAL_KEY);
                showGuideTab(false);
                showPanels();
            });
            guideTab.setContentView(tab, new android.view.ViewGroup.LayoutParams((int) (52 * dp), (int) (52 * dp)));
            android.view.Window w = guideTab.getWindow();
            // a small window: touches beside it go to the guide, keys stay with the game
            w.addFlags(WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE | WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL);
            w.clearFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND);
            w.setBackgroundDrawable(new android.graphics.drawable.ColorDrawable(0));
            WindowManager.LayoutParams lp = w.getAttributes();
            lp.width = android.view.ViewGroup.LayoutParams.WRAP_CONTENT;
            lp.height = android.view.ViewGroup.LayoutParams.WRAP_CONTENT;
            lp.gravity = android.view.Gravity.BOTTOM | android.view.Gravity.END;
            lp.x = (int) (16 * dp);
            lp.y = (int) (16 * dp);
            w.setAttributes(lp);
            guideTab.setCanceledOnTouchOutside(false);
        }
        guideTab.show();
    }

    // ---- FPS counter and rumble (polled from the host every 50 ms)
    private android.widget.TextView fpsView;
    private int rumble, lastFrames = -1, lastRumble;
    private boolean crt;
    private long lastFpsAt, lastRumbleAt;
    private final Runnable poller = new Runnable() {
        @Override public void run() {
            try {
                long now = android.os.SystemClock.uptimeMillis();
                if (fpsView != null && now - lastFpsAt >= 500) {
                    int f = nativeFrames();
                    if (lastFrames >= 0) fpsView.setText(Math.round((f - lastFrames) * 1000.0 / (now - lastFpsAt)) + " FPS");
                    lastFrames = f;
                    lastFpsAt = now;
                }
                // a text field selected (a character's name): Android's keyboard when there is no controller
                String text = nativeTextRequest();
                if (text != null && (touchMode == 1 || !gamepadConnected())) showTextDialog(text);
                if (textDialog != null && !nativeTextOpen()) { textDialog.dismiss(); textDialog = null; }
                if (rumble != 0) {
                    int r = nativeRumble();
                    // a level held by the game is renewed in slices; zero stops it
                    if (r != lastRumble || (r != 0 && now - lastRumbleAt >= 100)) { lastRumble = r; lastRumbleAt = now; vibrate(Math.max(r >> 8, r & 0xFF)); }
                }
            } catch (UnsatisfiedLinkError ignored) { }
            ui.postDelayed(this, 50);
        }
    };

    // ---- text fields (issue 6): the game's naming screen expects a PC keyboard
    static native String nativeTextRequest();
    static native boolean nativeTextOpen();
    static native void nativeTextReply(String text);
    private android.app.AlertDialog textDialog;

    /** Android's keyboard for the game's selected text field. OK puts the text in the field and
     *  confirms it (GitHub issue 6); Cancel keeps what was there (Cancel in the game opens this again). */
    private void showTextDialog(String current) {
        if (textDialog != null) textDialog.dismiss();
        android.widget.EditText e = new android.widget.EditText(this);
        e.setSingleLine(true);
        e.setText(current);
        e.setSelection(current.length());
        e.setFilters(new android.text.InputFilter[] { new android.text.InputFilter.LengthFilter(12) });
        e.setImeOptions(android.view.inputmethod.EditorInfo.IME_ACTION_DONE);
        int pad = (int) (20 * getResources().getDisplayMetrics().density);
        android.widget.FrameLayout box = new android.widget.FrameLayout(this);
        box.setPadding(pad, pad / 2, pad, 0);
        box.addView(e);
        android.app.AlertDialog d = new android.app.AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
                .setTitle("Name")
                .setView(box)
                .setPositiveButton("OK", (x, w) -> nativeTextReply("" + e.getText().toString()))   // : OK confirms the name
                .setNegativeButton("Cancel", (x, w) -> nativeTextReply(current))
                .setOnCancelListener(x -> nativeTextReply(current))
                .create();
        e.setOnEditorActionListener((v, action, ev) -> {
            if (action != android.view.inputmethod.EditorInfo.IME_ACTION_DONE) return false;
            nativeTextReply("" + e.getText().toString());
            d.dismiss();
            return true;
        });
        d.setOnDismissListener(x -> { if (textDialog == d) textDialog = null; hideSystemUi(); });
        d.getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_VISIBLE);
        textDialog = d;
        d.show();
        e.requestFocus();
    }

    /** The game's rumble on the controller's motor when it has one, else the device's. */
    private void vibrate(int strength) {
        android.os.Vibrator v = null;
        for (int id : InputDevice.getDeviceIds()) {
            InputDevice d = InputDevice.getDevice(id);
            if (isGamepad(d) && d.getVibratorManager().getVibratorIds().length > 0) { v = d.getVibratorManager().getDefaultVibrator(); break; }
        }
        if (v == null) v = ((android.os.VibratorManager) getSystemService(VIBRATOR_MANAGER_SERVICE)).getDefaultVibrator();
        if (v == null || !v.hasVibrator()) return;
        if (strength == 0) { v.cancel(); return; }
        int amp = Math.max(1, Math.min(255, rumble == 2 ? strength : strength * 2 / 3));   // Light: two thirds
        // the game sets a motor level and later zero: vibrate in short slices while it is on
        v.vibrate(android.os.VibrationEffect.createOneShot(120, v.hasAmplitudeControl() ? amp : android.os.VibrationEffect.DEFAULT_AMPLITUDE));
    }

    /** The display mode at this resolution nearest the game's frame rate: 120 Hz for Smooth 120,
     *  else 60 (the Thor defaults to 120; 60 saves power when the game draws 60 or fewer). */
    private void preferRefreshRate(float want) {
        android.view.Display d = getDisplay();
        if (d == null) return;
        android.view.Display.Mode cur = d.getMode(), best = null;
        for (android.view.Display.Mode m : d.getSupportedModes())
            if (m.getPhysicalWidth() == cur.getPhysicalWidth() && m.getPhysicalHeight() == cur.getPhysicalHeight()
                    && (best == null || Math.abs(m.getRefreshRate() - want) < Math.abs(best.getRefreshRate() - want))) best = m;
        if (best == null) return;
        WindowManager.LayoutParams lp = getWindow().getAttributes();
        lp.preferredDisplayModeId = best.getModeId();
        getWindow().setAttributes(lp);
        Log.i(TAG, "display mode " + best.getRefreshRate() + " Hz");
    }

    // ---- touch controls and controller detection
    private TouchControls touch;
    private int touchMode, touchButtons, touchLt, touchRt, touchRx, touchRy;

    private static boolean isGamepad(InputDevice d) {
        if (d == null || d.isVirtual()) return false;
        int src = d.getSources();
        return (src & InputDevice.SOURCE_GAMEPAD) == InputDevice.SOURCE_GAMEPAD || (src & InputDevice.SOURCE_JOYSTICK) == InputDevice.SOURCE_JOYSTICK;
    }

    private static boolean gamepadConnected() {
        for (int id : InputDevice.getDeviceIds()) if (isGamepad(InputDevice.getDevice(id))) return true;
        return false;
    }

    private void updateTouch() {
        if (touch == null) return;
        boolean show = touchMode == 1 || (touchMode == 0 && companion == null && companionHost == null && !gamepadConnected());
        touch.setVisibility(show ? View.VISIBLE : View.GONE);
        if (!show && (touchButtons | touchLt | touchRt | touchRx | touchRy) != 0) { touchButtons = touchLt = touchRt = touchRx = touchRy = 0; send(); }
    }

    private final android.hardware.input.InputManager.InputDeviceListener deviceListener =
            new android.hardware.input.InputManager.InputDeviceListener() {
        @Override public void onInputDeviceAdded(int id) { updateTouch(); }
        @Override public void onInputDeviceRemoved(int id) { updateTouch(); }
        @Override public void onInputDeviceChanged(int id) { updateTouch(); }
    };

    // ---- the screen's real refresh rate (FF7Recomp #46): the game draws one frame per vsync, so
    // when the system holds the screen at 60 Hz (power saving, a refresh-rate setting, a game
    // booster) 120 can't go above 60 however the app asks. Every 10 s the rate is counted over one
    // second of vsyncs; changes go to files/display.txt, which Report a problem includes. With
    // 120 chosen and the screen held at 60 Hz, the player is told once.
    private static final long DISPLAY_EVERY_MS = 10_000;
    private int displayHzSeen = -1, displayLowSamples, displayFps;
    private boolean displayToldLow, displayWatching, displayFileStarted;
    private final Runnable displaySample = this::sampleDisplayRate;

    private void startDisplayWatch(int fps) {
        displayFps = fps;
        android.view.Display d = getDisplay();
        StringBuilder s = new StringBuilder();
        s.append("display ").append(d == null ? "?" : d.getDisplayId());
        if (d != null) {
            s.append(": mode ").append(d.getMode().getModeId()).append(" @ ").append(Math.round(d.getMode().getRefreshRate())).append(" Hz; modes");
            for (android.view.Display.Mode m : d.getSupportedModes())
                s.append(' ').append(m.getModeId()).append('=').append(m.getPhysicalWidth()).append('x')
                 .append(m.getPhysicalHeight()).append('@').append(Math.round(m.getRefreshRate()));
        }
        android.os.PowerManager pm = (android.os.PowerManager) getSystemService(POWER_SERVICE);
        if (pm != null) s.append("; power saving ").append(pm.isPowerSaveMode() ? "on" : "off")
                         .append(", thermal status ").append(pm.getCurrentThermalStatus());
        s.append("; frame rate setting ").append(fps);
        displayLog(s.toString());
        displayWatching = true;
        ui.postDelayed(displaySample, 3000);
    }

    private void sampleDisplayRate() {
        if (!displayWatching) return;
        final android.view.Choreographer ch = android.view.Choreographer.getInstance();
        ch.postFrameCallback(new android.view.Choreographer.FrameCallback() {
            long first; int frames;
            @Override public void doFrame(long t) {
                if (!displayWatching) return;
                if (first == 0) first = t;
                else frames++;
                if (t - first < 1_000_000_000L) { ch.postFrameCallback(this); return; }
                displayMeasured(Math.round(frames * 1e9f / (t - first)));
                ui.postDelayed(displaySample, DISPLAY_EVERY_MS);
            }
        });
    }

    private void displayMeasured(int rate) {
        android.view.Display d = getDisplay();
        int modeHz = d == null ? 0 : Math.round(d.getMode().getRefreshRate());
        if (Math.abs(rate - displayHzSeen) > 5) {
            displayHzSeen = rate;
            android.os.PowerManager pm = (android.os.PowerManager) getSystemService(POWER_SERVICE);
            displayLog("screen at " + rate + " Hz (mode " + modeHz + " Hz" + (pm != null && pm.isPowerSaveMode() ? ", power saving on" : "") + ")");
        }
        float best = 0;
        if (d != null) for (android.view.Display.Mode m : d.getSupportedModes()) best = Math.max(best, m.getRefreshRate());
        boolean low = displayFps == 2 && rate <= 70 && best >= 90;      // 120 chosen, the screen held at 60
        displayLowSamples = low ? displayLowSamples + 1 : 0;
        if (displayLowSamples >= 2 && !displayToldLow && hasWindowFocus()) {
            displayToldLow = true;
            displayLog("told the player: 120 chosen, the screen is held at " + rate + " Hz");
            android.widget.Toast.makeText(this, "Your screen is running at 60 Hz, so 120 FPS can't go above 60. "
                    + "Turn off power saving and set the screen's refresh rate to its highest, or choose 60 FPS.",
                    android.widget.Toast.LENGTH_LONG).show();
        }
    }

    private void displayLog(String line) {
        Log.i(TAG, line);
        File f = new File(new GameInstall(this).files, "display.txt");
        boolean append = displayFileStarted;   // a new file each run
        displayFileStarted = true;
        try (java.io.FileWriter w = new java.io.FileWriter(f, append)) {
            w.write(new java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.US).format(new java.util.Date()) + " " + line + "\n");
        } catch (java.io.IOException e) {
            Log.w(TAG, "display.txt: " + e);
        }
    }

    // Home (or any switch away from the game): the panels go with it. A Presentation belongs to
    // the activity but stays on its display when the activity is stopped, so the bottom screen
    // kept showing them over Android's home screen (GitHub issue 16).
    private boolean panelsHidden, guideTabHidden, hostSentBack;

    // ---- the game moved to the other screen after the panels were placed (the Ayaneo Pocket DS
    // can move it after it starts; FF7Recomp's GitHub #49 saw the panels left covering the game):
    // the panels go to the screen the game is not on.
    private int panelsFor = -1;

    @Override
    public void onConfigurationChanged(android.content.res.Configuration c) {
        super.onConfigurationChanged(c);
        if (portrait && isTall() != arrangedTall) arrange(isTall());   // turned upright or sideways
        ui.post(this::followGameDisplay);
    }

    private void followGameDisplay() {
        if (panelsFor < 0 || isFinishing()) return;
        android.view.Display mine = getDisplay();
        if (mine == null || mine.getDisplayId() == panelsFor) return;
        displayLog("game moved from display " + panelsFor + " to " + mine.getDisplayId() + ": panels placed again");
        if (companion != null) { companion.dismiss(); companion = null; }
        if (guideTab != null) { guideTab.dismiss(); guideTab = null; }
        if (companionHost != null) { Activity h = companionHost; companionHost = null; h.finish(); }
        companionOn = false;
        panelsFor = -1;
        showCompanion();
    }

    @Override
    protected void onStop() {
        if (companion != null && companion.isShowing()) { companion.hide(); panelsHidden = true; }
        if (guideTab != null && guideTab.isShowing()) { guideTab.hide(); guideTabHidden = true; }
        if (companionHost != null && !companionHost.isFinishing()) { companionHost.moveTaskToBack(true); hostSentBack = true; }
        super.onStop();
    }

    @Override
    protected void onStart() {
        super.onStart();
        if (panelsHidden && companion != null) companion.show();
        if (guideTabHidden && guideTab != null) guideTab.show();
        if (hostSentBack && companionHost != null && !companionHost.isFinishing()) {
            try {
                android.app.ActivityOptions o = android.app.ActivityOptions.makeBasic();
                o.setLaunchDisplayId(android.view.Display.DEFAULT_DISPLAY);
                startActivity(new android.content.Intent(this, CompanionHostActivity.class)
                        .addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK | android.content.Intent.FLAG_ACTIVITY_REORDER_TO_FRONT | android.content.Intent.FLAG_ACTIVITY_NO_ANIMATION), o.toBundle());
            } catch (RuntimeException e) { Log.w(TAG, "panels not brought back: " + e); }
        }
        panelsHidden = guideTabHidden = hostSentBack = false;
    }

    // ---- Steam Cloud: Upload goes to Steam at once (GitHub issue 21)
    // The title screen's Upload writes FF9_CloudSaveFile_ww.sav into the Cloud folder (engine
    // Native/Compat.cs); the launcher used to send it to Steam only after the game closed, so a
    // device left running or asleep kept it. Now the write itself starts the launcher's sync
    // (SteamFiles.syncCloud, both directions as before), with a toast. The launcher's syncs
    // before Play and after the game stay as the safety net. Nothing is written to the launcher's
    // settings from here: this process's copy of them could overwrite the launcher's.
    private android.os.FileObserver cloudWatch;
    private final Object cloudLock = new Object();
    private boolean cloudBusy, cloudAgain;
    private static final java.util.regex.Pattern CLOUD_SAVE = java.util.regex.Pattern.compile("(?i)ff9_cloudsavefile_(ww|jp)\\.sav");

    private void watchCloud(File dir) {
        if (cloudWatch != null) return;
        cloudWatch = new android.os.FileObserver(dir, android.os.FileObserver.CLOSE_WRITE) {   // not MOVED_TO: the sync's own download is a rename
            @Override public void onEvent(int event, String path) {
                if (path != null && CLOUD_SAVE.matcher(path).matches()) uploadCloud(dir);
            }
        };
        cloudWatch.startWatching();
        android.util.Log.i(TAG, "cloud: uploads go to Steam at once (" + dir + ")");
    }

    private void cloudToast(String m) {
        ui.post(() -> android.widget.Toast.makeText(this, m, android.widget.Toast.LENGTH_LONG).show());
    }

    private boolean online() {
        android.net.ConnectivityManager cm = getSystemService(android.net.ConnectivityManager.class);
        android.net.NetworkCapabilities c = cm == null ? null : cm.getNetworkCapabilities(cm.getActiveNetwork());
        return c != null && c.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET)
                && c.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_VALIDATED);
    }

    private void uploadCloud(File dir) {
        synchronized (cloudLock) {
            if (cloudBusy) { cloudAgain = true; return; }     // written again while sending: send once more after
            cloudBusy = true;
        }
        new Thread(() -> {
            while (true) {
                synchronized (cloudLock) { cloudAgain = false; }
                String m;
                if (!online()) {
                    m = "Steam Cloud: you're offline. Your Cloud save goes to Steam when you leave the game while online.";
                } else {
                    cloudToast("Sending your Cloud save to Steam...");
                    try {
                        SteamFiles.CloudResult r = new SteamFiles(new SteamLink(this)).syncCloud(dir);
                        m = !r.up.isEmpty() && r.down.isEmpty() ? "Your Cloud save is on Steam." : "Steam Cloud: " + r;
                    } catch (Exception e) {
                        android.util.Log.w(TAG, "cloud upload", e);
                        m = "Steam Cloud upload failed: " + SteamLink.message(e) + ". The launcher tries again when you leave the game.";
                    }
                }
                android.util.Log.i(TAG, "cloud: " + m);
                cloudToast(m);
                synchronized (cloudLock) {
                    if (!cloudAgain) { cloudBusy = false; return; }
                }
            }
        }, "cloud-upload").start();
    }

    @Override
    protected void onDestroy() {
        if (cloudWatch != null) { cloudWatch.stopWatching(); cloudWatch = null; }
        displayWatching = false;
        ui.removeCallbacks(displaySample);
        try { unregisterReceiver(testPad); } catch (Exception ignored) { }
        if (companion != null) { companion.dismiss(); companion = null; }
        if (guideTab != null) { guideTab.dismiss(); guideTab = null; }
        if (companionHost != null) { companionHost.finish(); companionHost = null; }
        if (current == this) current = null;
        super.onDestroy();
    }

    /** Setup failed before the game could start: say what happened instead of a black screen. */
    private void showError(String title, String detail) {
        runOnUiThread(() -> {
            android.widget.TextView t = new android.widget.TextView(this);
            t.setText(title + "\n\n" + detail + "\n\nPress B to go back. Report a problem in the launcher sends the details.");
            t.setTextColor(android.graphics.Color.WHITE);
            t.setTextSize(18);
            t.setPadding(60, 60, 60, 60);
            t.setBackgroundColor(android.graphics.Color.rgb(12, 14, 24));
            root.addView(t);
            failed = true;
        });
    }
    private boolean failed;

    private void hideSystemUi() {
        // hide the status and navigation bars; a swipe from the edge shows them for a moment
        android.view.WindowInsetsController c = getWindow().getInsetsController();
        if (c == null) return;
        c.hide(android.view.WindowInsets.Type.systemBars());
        c.setSystemBarsBehavior(android.view.WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
    }

    @Override
    public void surfaceCreated(SurfaceHolder h) {
        System.loadLibrary("ff9");
        nativeSetSurface(h.getSurface());
        if (started) return;
        started = true;
        GameInstall gi = new GameInstall(this);
        new Thread(() -> {
            try {
                gi.extractRuntime();
                String nat = getApplicationInfo().nativeLibraryDir;
                Os.setenv("FF9_BCL", new File(gi.runtime, "bcl").getPath(), true);
                Os.setenv("FF9_ENGINE", new File(gi.runtime, "engine").getPath(), true);
                Os.setenv("FF9_NATIVE", nat, true);
                Os.setenv("FF9_MANAGED", new File(gi.game, "x64/FF9_Data/Managed").getPath(), true);
                Os.setenv("FF9_GAME", gi.game.getPath(), true);
                Os.setenv("LD_LIBRARY_PATH", nat, true);
                Os.setenv("FF9_COMPANION", "1", true);
                Os.setenv("FF9_WORLDMAP_DIR", new File(gi.files, "worldmap").getPath(), true);   // the panels' world map (Core/Companion.cs)
                Os.setenv("FF9_UI_SOUNDS", Sfx.dir(this).getPath(), true);   // the launcher's menu sounds (Core/UiSounds.cs)
                // Settings > Language (engine Native/Compat.cs; -1: the game's own choice)
                Os.setenv("FF9_LANGUAGE", String.valueOf(getSharedPreferences("launcher", MODE_PRIVATE).getInt("language", -1)), true);
                // Settings > Picture: narrow (4:3) areas stretched to the screen (engine Gfx/Pipeline.cs)
                Os.setenv("FF9_STRETCH", String.valueOf(getSharedPreferences("launcher", MODE_PRIVATE).getInt("stretch", 0)), true);
                Os.setenv("FF9_ACHIEVEMENTS", Achievements.file(this).getPath(), true);   // src/host/win32.c     // the game's state for the panels and save-state names
                // Steam Cloud saves. Turns on the title screen's Cloud menu (src/host/win32.c) and tells the engine which folder it should use (Native/Compat.cs).
                // The launcher syncs that folder with Steam.
                if (Settings.cloudSaves(this) && new SteamLink(this).signedIn()) {
                    File cloud = SteamFiles.cloudDir(this);
                    cloud.mkdirs();
                    Os.setenv("FF9_CLOUD_ENABLED", "1", true);
                    Os.setenv("FF9_CLOUD_DIR", cloud.getPath(), true);
                    if (Settings.cloudUploadNow(this)) watchCloud(cloud);
                } else {     // Clear them, in case this process ran the game earlier with them set.
                    Os.unsetenv("FF9_CLOUD_ENABLED");
                    Os.unsetenv("FF9_CLOUD_DIR");
                }
                // resume a save state, or Settings > Start: Continue (Memoria loads its autosave at once)
                boolean autoload = getIntent().getBooleanExtra("autoload", false)
                        || getSharedPreferences("launcher", MODE_PRIVATE).getInt("start", 0) == 1;
                if (autoload && SaveStates.autosave(this).isFile()) Os.setenv("FF9_AUTOLOAD", "1", true);   // loaded from the title (engine Core/TestLoad.cs)
                String testLoad = getIntent().getStringExtra("test_load");
                if (testLoad != null && testLoad.matches("\\d+:\\d+")) Os.setenv("FF9_LOAD_FILE", "900:" + testLoad, true);
                String testArgs = getIntent().getStringExtra("test_args");
                if (testArgs != null) Os.setenv("FF9_ARGS", testArgs.trim().replace(' ', '+'), true);
                // adb tests: files/env.txt, one FF9_TEST_NAME=value per line (FF9_TEST_MAX_TEXTURE=4096
                // tries the large-texture fallback on the Thor, whose GPU takes 16384)
                File env = new File(gi.files, "env.txt");
                if (env.isFile())
                    for (String l : new String(java.nio.file.Files.readAllBytes(env.toPath()), java.nio.charset.StandardCharsets.UTF_8).split("\n")) {
                        int eq = l.indexOf('=');
                        if (eq > 0 && (l.startsWith("FF9_TEST_") || l.startsWith("FF9_TRACE_"))) Os.setenv(l.substring(0, eq).trim(), l.substring(eq + 1).trim(), true);
                    }
            } catch (Exception e) {
                Log.e(TAG, "runtime setup failed", e);
                Reports.writeText(new File(gi.files, "setup-error.txt"), Log.getStackTraceString(e));
                showError("The game could not start", "Setting up the runtime failed: " + e.getMessage());
                return;
            }
            File log = new File(gi.files, "ff9.log");
            if (log.isFile()) log.renameTo(new File(gi.files, "ff9.prev.log"));   // the run before, for reports
            int r = nativeRun(gi.files.getPath(), log.getPath());
            Log.i(TAG, "game exited " + r);
            runOnUiThread(this::finish);
            android.os.Process.killProcess(android.os.Process.myPid());
        }, "FF9").start();
    }

    @Override public void surfaceChanged(SurfaceHolder h, int f, int w, int hh) { nativeSetSurface(h.getSurface()); }
    /** The window goes (sleep, background): the engine drops its surface and the game pauses
     *  until a new one comes (src/host/android_jni.c, Gfx/Device.cs). */
    @Override public void surfaceDestroyed(SurfaceHolder h) { nativeSetSurface(null); }

    @Override
    public void onWindowFocusChanged(boolean focus) {
        super.onWindowFocusChanged(focus);
        if (focus) hideSystemUi();
    }

    private static int bit(int code) {
        switch (code) {
            case KeyEvent.KEYCODE_BUTTON_A: return A;
            case KeyEvent.KEYCODE_BUTTON_B: return B;
            case KeyEvent.KEYCODE_BUTTON_X: return X;
            case KeyEvent.KEYCODE_BUTTON_Y: return Y;
            case KeyEvent.KEYCODE_BUTTON_L1: return LB;
            case KeyEvent.KEYCODE_BUTTON_R1: return RB;
            case KeyEvent.KEYCODE_BUTTON_START: return START;
            case KeyEvent.KEYCODE_BUTTON_SELECT: case KeyEvent.KEYCODE_BACK: return BACK;
            case KeyEvent.KEYCODE_BUTTON_THUMBL: return LTHUMB;
            case KeyEvent.KEYCODE_BUTTON_THUMBR: return RTHUMB;
            case KeyEvent.KEYCODE_DPAD_UP: return UP;
            case KeyEvent.KEYCODE_DPAD_DOWN: return DOWN;
            case KeyEvent.KEYCODE_DPAD_LEFT: return LEFT;
            case KeyEvent.KEYCODE_DPAD_RIGHT: return RIGHT;
        }
        return 0;
    }

    // ---- a physical keyboard: arrows/WASD move, Enter/Space confirm, Esc/Backspace cancel,
    // Tab = menu, Q/E = L1/R1, Shift = the fourth button, Backslash = Select, P = Start
    private int keyButtons;

    private static int keyboardBit(int code) {
        switch (code) {
            case KeyEvent.KEYCODE_DPAD_UP: case KeyEvent.KEYCODE_W: return UP;
            case KeyEvent.KEYCODE_DPAD_DOWN: case KeyEvent.KEYCODE_S: return DOWN;
            case KeyEvent.KEYCODE_DPAD_LEFT: case KeyEvent.KEYCODE_A: return LEFT;
            case KeyEvent.KEYCODE_DPAD_RIGHT: case KeyEvent.KEYCODE_D: return RIGHT;
            case KeyEvent.KEYCODE_ENTER: case KeyEvent.KEYCODE_SPACE: case KeyEvent.KEYCODE_NUMPAD_ENTER: return A;
            case KeyEvent.KEYCODE_ESCAPE: case KeyEvent.KEYCODE_DEL: return B;
            case KeyEvent.KEYCODE_TAB: return Y;
            case KeyEvent.KEYCODE_SHIFT_LEFT: case KeyEvent.KEYCODE_SHIFT_RIGHT: return X;
            case KeyEvent.KEYCODE_Q: return LB;
            case KeyEvent.KEYCODE_E: return RB;
            case KeyEvent.KEYCODE_BACKSLASH: return BACK;
            case KeyEvent.KEYCODE_P: return START;
        }
        return 0;
    }

    private void send() {
        int all = buttons | dpad | testButtons | keyButtons | touchButtons;
        // Select+Start together is ours (leave the game): the game sees neither while both are held
        int toGame = (all & (START | BACK)) == (START | BACK) ? all & ~(START | BACK) : all;
        // the on-screen right stick when the real one is centred
        boolean realStick = rx != 0 || ry != 0;
        nativePad(toGame, Math.max(lt, touchLt), Math.max(rt, touchRt), lx, ly, realStick ? rx : touchRx, realStick ? ry : touchRy);
        // Select+L1: the guide or the panels on the second screen
        boolean guideCombo = (all & (BACK | LB)) == (BACK | LB);
        if (guideCombo && !guideHeld) toggleGuide();
        guideHeld = guideCombo;
        // Select+R1: keep the current place in a save-state slot
        boolean keepCombo = (all & (BACK | RB)) == (BACK | RB);
        if (keepCombo && !keepHeld) keepState();
        keepHeld = keepCombo;
        // Select+Start held for a second: the leave-the-game dialog
        boolean combo = (all & (START | BACK)) == (START | BACK);
        if (combo != quitHeld) {
            quitHeld = combo;
            if (combo) ui.postDelayed(askQuit, 1000);
            else ui.removeCallbacks(askQuit);
        }
    }

    // ---- leaving the game: Select+Start held, then Leave. Ends the game's process (":game");
    // the launcher (its own process) is back. Unsaved progress is lost, as the dialog says.
    // ---- the panels' deck and book (CompanionView), on the UI thread
    static boolean hasGuide() { GameActivity g = current; return g != null && g.guide != null; }
    static void toggleGuideFromPanel() { GameActivity g = current; if (g != null) g.toggleGuide(); }
    static void commandFromPanel(int cmd) { try { nativeCommand(cmd); } catch (UnsatisfiedLinkError ignored) { } }

    /** The game's own button, pressed for a moment (Menu: Y, the game's menu button). */
    static void tapFromPanel(int bits) {
        GameActivity g = current;
        if (g == null) return;
        g.ui.removeCallbacks(g.testRelease);
        g.testButtons = bits;
        g.send();
        g.ui.postDelayed(g.testRelease, 120);
    }

    static void keepStateFromPanel(java.util.function.Consumer<String> note) {
        GameActivity g = current;
        if (g != null) g.keepState(note);
    }

    /** Load: the newest save state, put back as Memoria's autosave and loaded in game. */
    static void loadLatestFromPanel(java.util.function.Consumer<String> note) {
        GameActivity g = current;
        if (g == null) return;
        int best = -1;
        long newest = 0;
        for (int i = 1; i <= SaveStates.SLOTS; i++)
            if (SaveStates.used(g, i) && SaveStates.data(g, i).lastModified() > newest) { newest = SaveStates.data(g, i).lastModified(); best = i; }
        if (best < 0) { note.accept("No save states yet (Save keeps one)"); return; }
        try {
            SaveStates.restore(g, best);
            commandFromPanel(3);
            String d = SaveStates.describe(g, best);
            note.accept("Loading slot " + best + (d == null ? "" : ": " + d.split(",")[0]));
        } catch (Exception e) {
            note.accept("Could not load: " + e.getMessage());
        }
    }

    private boolean keepHeld, guideHeld;
    private android.webkit.WebView guide;
    private String guidePending;

    private void toggleGuide() {
        if (guide == null) return;
        if (guidePending != null) { guide.loadUrl(guidePending); guidePending = null; }
        guide.setVisibility(guide.getVisibility() == View.VISIBLE ? View.GONE : View.VISIBLE);
    }

    private void keepState() { keepState(null); }

    private void keepState(java.util.function.Consumer<String> note) {
        String place = "";
        try { place = CompanionView.clean(new org.json.JSONObject(String.valueOf(companionState())).optString("loc")); } catch (Exception ignored) { }
        String p = place;
        new Thread(() -> {
            String msg;
            try { msg = "Save state " + SaveStates.keep(this, p) + ": " + (p.isEmpty() ? "kept" : p); }
            catch (Exception e) { msg = "No save state: " + e.getMessage(); }
            String m = msg;
            runOnUiThread(() -> {
                if (note != null) note.accept(m);
                else android.widget.Toast.makeText(this, m, android.widget.Toast.LENGTH_SHORT).show();
            });
        }).start();
    }

    private final Runnable askQuit = this::askQuit;
    private boolean quitHeld;
    private android.app.AlertDialog quitDialog;

    private void askQuit() {
        quitHeld = false;
        if (isFinishing() || (quitDialog != null && quitDialog.isShowing())) return;
        buttons = dpad = testButtons = 0;
        lt = rt = 0;
        nativePad(0, 0, 0, lx, ly, rx, ry);
        quitDialog = new android.app.AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
                .setTitle("Leave the game?")
                .setMessage("You go back to the launcher. Anything since your last save is lost.")
                .setPositiveButton("Leave", (d, w) -> leaveGame())
                .setNegativeButton("Keep playing", null)
                .show();
        android.widget.Button keep = quitDialog.getButton(android.app.AlertDialog.BUTTON_NEGATIVE);
        android.widget.Button leave = quitDialog.getButton(android.app.AlertDialog.BUTTON_POSITIVE);
        // a pad drives it: left/right pick, A presses, B keeps playing; the picked one is underlined
        Runnable mark = () -> {
            for (android.widget.Button bt : new android.widget.Button[] { keep, leave })
                if (bt != null) bt.setPaintFlags(bt.isFocused() ? bt.getPaintFlags() | android.graphics.Paint.UNDERLINE_TEXT_FLAG
                                                                : bt.getPaintFlags() & ~android.graphics.Paint.UNDERLINE_TEXT_FLAG);
        };
        if (keep != null) { keep.setFocusableInTouchMode(true); keep.requestFocus(); }
        if (leave != null) leave.setFocusableInTouchMode(true);
        mark.run();
        quitDialog.setOnKeyListener((d, code, ev) -> {
            if (ev.getAction() != KeyEvent.ACTION_DOWN) return code == KeyEvent.KEYCODE_BUTTON_A || code == KeyEvent.KEYCODE_BUTTON_B;
            switch (code) {
                case KeyEvent.KEYCODE_DPAD_LEFT: if (keep != null) keep.requestFocus(); mark.run(); return true;
                case KeyEvent.KEYCODE_DPAD_RIGHT: if (leave != null) leave.requestFocus(); mark.run(); return true;
                case KeyEvent.KEYCODE_BUTTON_A: case KeyEvent.KEYCODE_ENTER: case KeyEvent.KEYCODE_DPAD_CENTER:
                    if (leave != null && leave.isFocused()) leave.performClick(); else d.dismiss();
                    return true;
                case KeyEvent.KEYCODE_BUTTON_B: case KeyEvent.KEYCODE_BACK: d.dismiss(); return true;
            }
            return false;
        });
    }

    private void leaveGame() {
        Log.i(TAG, "leaving the game (Select+Start)");
        startActivity(new android.content.Intent(this, LauncherActivity.class).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK));
        finish();
        ui.postDelayed(() -> android.os.Process.killProcess(android.os.Process.myPid()), 300);
    }

    // ---- test API: adb holds pad buttons for a set time (`input keyevent` is too short for the
    // game to see reliably). Only the shell can send it (DUMP):
    //   am broadcast -a com.ff9recomp.test.PAD -p com.ff9recomp --ei buttons 0x1000 --ei ms 100
    private int testButtons;
    private final Runnable testRelease = () -> { testButtons = 0; send(); };
    private final android.content.BroadcastReceiver testPad = new android.content.BroadcastReceiver() {
        @Override public void onReceive(android.content.Context c, android.content.Intent i) {
            ui.removeCallbacks(testRelease);
            testButtons = i.getIntExtra("buttons", 0);
            send();
            ui.postDelayed(testRelease, Math.max(16, Math.min(10000, i.getIntExtra("ms", 100))));
        }
    };

    @Override
    public boolean dispatchKeyEvent(KeyEvent e) {
        if (failed) {
            if (e.getAction() == KeyEvent.ACTION_UP && (e.getKeyCode() == KeyEvent.KEYCODE_BUTTON_B || e.getKeyCode() == KeyEvent.KEYCODE_BACK)) finish();
            return true;
        }
        InputDevice dev = e.getDevice();
        if (dev != null && !dev.isVirtual() && !isGamepad(dev) && dev.getKeyboardType() == InputDevice.KEYBOARD_TYPE_ALPHABETIC) {
            int kb = keyboardBit(e.getKeyCode());
            if (kb == 0) return super.dispatchKeyEvent(e);
            if (e.getAction() == KeyEvent.ACTION_DOWN) keyButtons |= kb; else if (e.getAction() == KeyEvent.ACTION_UP) keyButtons &= ~kb;
            send();
            return true;
        }
        int b = bit(e.getKeyCode());
        if (b == 0) return super.dispatchKeyEvent(e);
        if (e.getKeyCode() == KeyEvent.KEYCODE_BUTTON_L2 || e.getKeyCode() == KeyEvent.KEYCODE_BUTTON_R2) return true;
        if (e.getAction() == KeyEvent.ACTION_DOWN) buttons |= b; else if (e.getAction() == KeyEvent.ACTION_UP) buttons &= ~b;
        send();
        return true;
    }

    private static int axis(float v) { return Math.max(-32767, Math.min(32767, Math.round(v * 32767))); }

    @Override
    public boolean onGenericMotionEvent(MotionEvent e) {
        if ((e.getSource() & InputDevice.SOURCE_JOYSTICK) == 0) return super.onGenericMotionEvent(e);
        lx = axis(e.getAxisValue(MotionEvent.AXIS_X));
        ly = -axis(e.getAxisValue(MotionEvent.AXIS_Y));       // XInput: up is positive
        rx = axis(e.getAxisValue(MotionEvent.AXIS_Z));
        ry = -axis(e.getAxisValue(MotionEvent.AXIS_RZ));
        lt = Math.round(Math.max(e.getAxisValue(MotionEvent.AXIS_LTRIGGER), e.getAxisValue(MotionEvent.AXIS_BRAKE)) * 255);
        rt = Math.round(Math.max(e.getAxisValue(MotionEvent.AXIS_RTRIGGER), e.getAxisValue(MotionEvent.AXIS_GAS)) * 255);
        float hx = e.getAxisValue(MotionEvent.AXIS_HAT_X), hy = e.getAxisValue(MotionEvent.AXIS_HAT_Y);
        dpad = (hx < -0.5f ? LEFT : 0) | (hx > 0.5f ? RIGHT : 0) | (hy < -0.5f ? UP : 0) | (hy > 0.5f ? DOWN : 0);
        send();
        return true;
    }
}
