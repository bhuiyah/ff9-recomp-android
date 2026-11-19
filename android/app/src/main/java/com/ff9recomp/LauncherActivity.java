package com.ff9recomp;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.LayerDrawable;
import android.graphics.drawable.StateListDrawable;
import android.net.Uri;
import android.os.Bundle;
import android.text.SpannableStringBuilder;
import android.text.Spanned;
import android.text.style.ForegroundColorSpan;
import android.text.style.RelativeSizeSpan;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.CompoundButton;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.RadioButton;
import android.widget.RadioGroup;
import android.widget.ScrollView;
import android.widget.TextView;

import java.io.File;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * The entry point, laid out like FF7Recomp's launcher (the game runs in its own ":game" process,
 * so the launcher survives a crash): the command window on the left (Play, Mods, Save states,
 * Achievements, Settings, Game files, Report a problem, Quit), a one-line help window on top,
 * and the page's window on the right. FF9's look: slate windows with a pale border, gold accents,
 * the hand cursor on the commands. Everything works with a controller: the D-pad moves, A
 * confirms, B goes back to the commands.
 */
public class LauncherActivity extends Activity {
    private static final int PICK_GAME = 1, PICK_SAVE = 2, EXPORT_SAVES = 3;
    private static final int BG = Color.rgb(8, 9, 14), TEXT = 0xFFF2EEE2, DIM = 0xFFAEB0BC, ACCENT = 0xFFE8C66A;
    private static final int WIN_TOP = 0xFF5E6478, WIN_BOTTOM = 0xFF22252F, HOT_TOP = 0xFF8A8466, HOT_BOTTOM = 0xFF3E3A2A,
            BTN_TOP = 0xFF4A4F60, BTN_BOTTOM = 0xFF262935, BORDER = 0xFFDCD6C4, INNER = 0xFF3A3F50;

    private GameInstall install;
    private TextView status;
    private FrameLayout content;
    private Button playButton;
    private final Map<String, Button> nav = new HashMap<>();
    private String page = "settings";
    private boolean busy, withSaves, smallScreen;
    private ModCatalog.Mod modShown;
    private String testArgs, testLoad;

    private static final int SMALL_WIDTH_DP = 600, COMPACT_WIDTH_DP = 560;
    private int testW, testH;

    /** Under 600dp wide (640x480 handhelds): the launcher laid out as about 560dp, so the side
     *  menu, text and buttons fit (FF7Recomp #47). TestControl SCREEN lays it out as another
     *  screen would, in the top-left corner. */
    @Override
    protected void attachBaseContext(android.content.Context base) {
        super.attachBaseContext(base);
        android.util.DisplayMetrics dm = base.getResources().getDisplayMetrics();
        int wPx = Math.max(dm.widthPixels, dm.heightPixels), dpi = dm.densityDpi;
        java.util.regex.Matcher m = java.util.regex.Pattern.compile("(\\d+)x(\\d+)@(\\d+)")
                .matcher(base.getSharedPreferences("launcher", MODE_PRIVATE).getString(TestControl.TEST_SCREEN, ""));
        if (m.matches()) {
            testW = Integer.parseInt(m.group(1));
            testH = Integer.parseInt(m.group(2));
            wPx = Math.max(testW, testH);
            dpi = Integer.parseInt(m.group(3));
        }
        if (wPx * 160f / dpi >= SMALL_WIDTH_DP) {
            if (testW == 0) return;
        } else smallScreen = true;
        int want = smallScreen ? Math.max(120, Math.round(wPx * 160f / COMPACT_WIDTH_DP)) : dpi;
        if (want < dpi || testW > 0) {
            android.content.res.Configuration o = new android.content.res.Configuration();
            o.densityDpi = Math.min(want, dpi);
            applyOverrideConfiguration(o);
        }
    }

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        Reports.installCrashHandler(this);
        install = new GameInstall(this);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.HORIZONTAL);
        root.setBackgroundColor(BG);
        if (smallScreen) root.setPadding(dp(8), dp(6), dp(8), dp(6));
        else root.setPadding(dp(24), dp(20), dp(24), dp(20));

        // left: the title and the command window
        LinearLayout side = column();
        side.setPadding(0, 0, dp(14), 0);
        TextView title = text("FINAL FANTASY IX", smallScreen ? 13 : 17, TEXT);
        title.setTypeface(Typeface.create("serif", Typeface.BOLD));
        title.setLetterSpacing(0.04f);
        title.setSingleLine(true);
        side.addView(title);
        side.addView(text("native ARM64 · Memoria" + (install.hasMoguri() ? " · Moguri" : ""), 11, ACCENT));
        side.addView(space(10));
        LinearLayout menu = column();
        menu.setBackground(window(WIN_TOP, WIN_BOTTOM, 8));
        menu.setPadding(dp(8), dp(6), dp(8), dp(6));
        playButton = menuItem("Play", null);
        playButton.setTextSize(19);
        playButton.setTypeface(Typeface.DEFAULT_BOLD);
        menu.addView(playButton);
        menu.addView(menuItem("Mods", "mods"));
        menu.addView(menuItem("Get mods", "catalog"));
        menu.addView(menuItem("Downloads", "downloads"));
        menu.addView(menuItem("Save states", "states"));
        menu.addView(menuItem("Achievements", "achievements"));
        menu.addView(menuItem("Settings", "settings"));
        menu.addView(menuItem("Game files", "game"));
        menu.addView(menuItem("Report a problem", "report"));
        Button quit = menuItem("Quit", null);
        quit.setOnClickListener(v -> quit());
        menu.addView(quit);
        side.addView(menu, new LinearLayout.LayoutParams(dp(smallScreen ? 150 : 200), ViewGroup.LayoutParams.WRAP_CONTENT));
        ScrollView sideScroll = new ScrollView(this);
        sideScroll.setVerticalScrollBarEnabled(false);
        sideScroll.addView(side);
        root.addView(sideScroll, new LinearLayout.LayoutParams(dp(smallScreen ? 160 : 214), ViewGroup.LayoutParams.MATCH_PARENT));

        // right: the help window over the page's window
        LinearLayout right = column();
        status = text("", 14, TEXT);
        status.setBackground(window(WIN_TOP, WIN_BOTTOM, 8));
        status.setPadding(dp(16), dp(8), dp(16), dp(8));
        status.setMaxLines(2);
        status.setEllipsize(android.text.TextUtils.TruncateAt.END);
        right.addView(status, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        right.addView(space(10));
        content = new FrameLayout(this);
        content.setBackground(window(WIN_TOP, WIN_BOTTOM, 10));
        if (smallScreen) content.setPadding(dp(10), dp(8), dp(10), dp(8));
        else content.setPadding(dp(22), dp(16), dp(22), dp(16));
        right.addView(content, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1));
        root.addView(right, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1));
        if (testW > 0) {   // TestControl SCREEN: that screen's size, in the top-left corner
            FrameLayout outer = new FrameLayout(this);
            outer.setBackgroundColor(0xFF303030);
            outer.addView(root, new FrameLayout.LayoutParams(Math.max(testW, testH), Math.min(testW, testH)));
            setContentView(outer);
        } else setContentView(root);
        hideSystemBars();

        if (!install.hasGame()) page = "game";
        refresh();
        if (Storage.sdMissing(this)) content.post(() -> message("SD card not found",
                "Your game, mods and saves are on an SD card that isn't inserted. Put it back in, or move the files to internal storage under Game files."));
        (ready() ? playButton : nav.get(page)).requestFocus();
        handleTestIntent(getIntent());
        if (!Updater.busy && System.currentTimeMillis() - Updater.lastCheck > 3600_000L) checkUpdates(false);
        DownloadQueue.get(this).setListener(this::queueChanged);
        if (ModCatalog.age(this) > 24 * 3600_000L) refreshCatalog(false);     // quietly, once a day
        // If the launcher was closed while the game was running, sync now so an Upload made then still reaches Steam.
        if (cloudSaves() && TestControl.gamePid(this) <= 0) cloudAfterGame = true;
        new Thread(() -> {    // achievements earned while playing go to Steam when the launcher is back
            try { SteamLink sl = new SteamLink(this); if (sl.signedIn() && sl.waiting() > 0) sl.sync(); } catch (Exception ignored) { }
        }).start();
    }

    @Override protected void onDestroy() {
        DownloadQueue.get(this).setListener(null);
        ticker.removeCallbacksAndMessages(null);
        super.onDestroy();
    }

    @Override protected void onNewIntent(Intent i) { super.onNewIntent(i); handleTestIntent(i); }

    @Override protected void onResume() {
        super.onResume();
        Sfx.load(this);
        hideSystemBars();
        if (!busy) { View f = getCurrentFocus(); refresh(); if (f != null && !inContent(f)) f.requestFocus(); }
        // Back from the game. Once it has closed (not just gone to the background), send what Upload wrote to Steam.
        if (cloudAfterGame && !busy && cloudSaves() && TestControl.gamePid(this) <= 0) {
            cloudAfterGame = false;
            syncCloud(null);
        }
    }

    /** adb test API: am start ... --ez test_play true presses Play once the launcher is ready. */
    private void handleTestIntent(Intent i) {
        if (i != null && i.getBooleanExtra(TestControl.EXTRA_PLAY, false)) {
            i.removeExtra(TestControl.EXTRA_PLAY);
            testArgs = i.getStringExtra("test_args");     // adb tests: Memoria's launch options (e.g. "-memoria-battle 336")
            i.removeExtra("test_args");
            testLoad = i.getStringExtra("test_load");     // adb tests: "slot:file" (0-based) loaded from the title (FF9_LOAD_FILE)
            i.removeExtra("test_load");
            content.post(this::play);
        }
    }

    private boolean ready() { return install.hasGame() && install.missingMessage() == null; }

    // ------------------------------------------------------------------ pages

    private void show(String which) { page = which; refresh(); focusContent(); }

    private String shownPage;

    private void refresh() {
        // the cursor and the scroll position stay where they were when the same page is rebuilt
        // (a mod switched on, a download finished): FF7Recomp #32/#47
        boolean same = page.equals(shownPage);
        String keepFocus = same ? focusKey() : null;
        int keepY = same && content.getChildCount() > 0 && content.getChildAt(0) instanceof ScrollView ? content.getChildAt(0).getScrollY() : 0;
        // Get mods scrolls its list and its categories on their own (the page itself doesn't)
        int keepList = same && catScroll != null ? catScroll.getScrollY() : 0, keepCats = same && catsScroll != null ? catsScroll.getScrollY() : 0;
        catScroll = catsScroll = null;
        shownPage = page;
        updateStatus();
        updateDownloadsLabel();
        playButton.setEnabled(ready() && !busy);
        content.removeAllViews();
        View v;
        switch (page) {
            case "mods": v = modsPage(); break;
            case "catalog": v = catalogPage(); break;
            case "downloads": v = downloadsPage(); break;
            case "mod": v = modPage(modShown); break;
            case "states": v = statesPage(); break;
            case "achievements": v = achievementsPage(); break;
            case "game": v = gamePage(); break;
            case "report": v = reportPage(); break;
            case "memoria": v = memoriaPage(); break;
            default: v = settingsPage(); break;
        }
        ScrollView sv = new ScrollView(this);
        sv.setFillViewport(true);
        if (page.equals("catalog")) {
            content.addView(v, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
            sv = catScroll;
            keepY = keepList;
            if (same && catsScroll != null) { final ScrollView cs = catsScroll; final int cy = keepCats; cs.post(() -> cs.scrollTo(0, cy)); }
        } else {
            sv.addView(v);
            content.addView(sv);
        }
        theme(content);
        String sel = page.equals("mod") ? modBack : page;
        for (Map.Entry<String, Button> e : nav.entrySet()) e.getValue().setSelected(e.getKey().equals(sel));
        if (same && sv != null) {
            View f = keepFocus == null ? null : restoreFocus(keepFocus);
            final int y = keepY;
            final ScrollView sv2 = sv;
            sv2.getViewTreeObserver().addOnPreDrawListener(new android.view.ViewTreeObserver.OnPreDrawListener() {
                @Override public boolean onPreDraw() {
                    sv2.getViewTreeObserver().removeOnPreDrawListener(this);
                    sv2.scrollTo(0, y);
                    if (f != null && f.getWidth() > 0) f.requestRectangleOnScreen(new android.graphics.Rect(0, 0, f.getWidth(), f.getHeight()), true);
                    return true;
                }
            });
        }
    }

    /** Where the focus is inside the page: a view's "focus:" tag, else its child-index path. */
    private String focusKey() {
        View f = getCurrentFocus();
        if (f == null || !inContent(f)) return null;
        StringBuilder path = new StringBuilder();
        for (View v = f; v != content; ) {
            if (v.getTag() instanceof String && ((String) v.getTag()).startsWith("focus:") && path.length() == 0) return (String) v.getTag();
            if (!(v.getParent() instanceof ViewGroup)) return null;
            ViewGroup g = (ViewGroup) v.getParent();
            path.insert(0, "/" + g.indexOfChild(v));
            v = g;
        }
        return path.toString();
    }

    /** Puts the focus back (a tag, else the same path, else the nearest earlier focusable). */
    private View restoreFocus(String key) {
        View target = null;
        if (key.startsWith("focus:")) target = content.findViewWithTag(key);
        else {
            View v = content;
            for (String part : key.substring(1).split("/")) {
                if (!(v instanceof ViewGroup)) break;
                ViewGroup g = (ViewGroup) v;
                int i = Integer.parseInt(part);
                if (i >= g.getChildCount()) { v = g.getChildAt(g.getChildCount() - 1); break; }
                v = g.getChildAt(i);
            }
            target = v;
        }
        if (target != null && !(target.isFocusable() && target.isEnabled())) target = firstFocusable(target);
        if (target == null) target = firstFocusable(content);
        if (target != null) target.requestFocus();
        return target;
    }

    private void updateStatus() {
        String game = !install.hasGame() ? "not imported" : install.needsMemoria() ? "Memoria missing" : install.missingMessage() != null ? "files missing" : "ready";
        int on = ModCatalog.enabled(this).size();
        StringBuilder s = new StringBuilder("Game: " + game + "   ·   Mods: " + on + " on   ·   Frame rate: " + Settings.valueText(this, Settings.find("fps")));
        Updater.Release up = Updater.available;
        if (up != null) s.append("   ·   Update ").append(up.version).append(" is out (Game files)");
        String exit = Reports.lastGameExit(this);
        String pend = JobService.pending(this);
        if (JobService.busy()) s.append("\n").append(JobService.status);
        else if (pend != null) s.append("\n").append(JobService.title(pend)).append(" is paused: Continue is under Game files.");
        if (exit != null && exit.startsWith("crashed")) s.append("\nThe game last ").append(exit).append(": Report a problem sends the details.");
        status.setText(s);
    }

    private void focusContent() {
        content.post(() -> {
            View f = firstFocusable(content);
            if (f != null) f.requestFocus();
        });
    }

    private static View firstFocusable(View v) {
        if (v instanceof ViewGroup && !(v instanceof CompoundButton)) {
            ViewGroup g = (ViewGroup) v;
            for (int i = 0; i < g.getChildCount(); i++) { View f = firstFocusable(g.getChildAt(i)); if (f != null) return f; }
            return null;
        }
        return v.isFocusable() && v.getVisibility() == View.VISIBLE && v.isEnabled() ? v : null;
    }

    // ------------------------------------------------------------------ play / quit

    private void play() { play(false); }

    private void play(boolean autoload) {
        if (busy) return;
        if (JobService.busy()) { status.setText(JobService.title(JobService.running) + ": wait for it to finish, or pause it (Game files)."); return; }
        if (!ready()) { show("game"); return; }
        try {
            Settings.apply(this, install.game);
            ModCatalog.sync(this);          // mods' options: Memoria's defaults for a mod new to Memoria.ini
        } catch (Exception e) {
            status.setText("Could not write the settings into Memoria.ini: " + e.getMessage());
            return;
        }
        Intent g = new Intent(this, GameActivity.class).putExtra("autoload", autoload);
        if (testArgs != null) { g.putExtra("test_args", testArgs); testArgs = null; }
        if (testLoad != null && testLoad.matches("\\d+:\\d+")) { g.putExtra("test_load", testLoad); testLoad = null; }
        // Fetch Steam's copy before the game starts so Download on the title screen gets it. If the sync fails, the game starts anyway.
        if (cloudSaves()) {
            syncCloud(() -> { cloudAfterGame = true; startGame(g); });
            return;
        }
        startGame(g);
    }

    private void startGame(Intent g) {
        if (Settings.prefs(this).getBoolean("swap_screens", false)) {
            android.hardware.display.DisplayManager dm = (android.hardware.display.DisplayManager) getSystemService(DISPLAY_SERVICE);
            android.view.Display[] ds = dm.getDisplays(android.hardware.display.DisplayManager.DISPLAY_CATEGORY_PRESENTATION);
            // not the main screen, which the Ayaneo Pocket DS also lists as a presentation display (issue 31)
            int other = GameActivity.otherScreen(ds, android.view.Display.DEFAULT_DISPLAY);
            if (other >= 0) {
                android.app.ActivityOptions o = android.app.ActivityOptions.makeBasic();
                o.setLaunchDisplayId(ds[other].getDisplayId());
                startActivity(g.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_MULTIPLE_TASK), o.toBundle());
                return;
            }
        }
        startActivity(g);
    }

    private void quit() {
        int pid = TestControl.gamePid(this);
        if (pid <= 0) { finishAndRemoveTask(); return; }
        ask("Close the game too?", "The game is still running. Anything since your last save (or the autosave) is lost.", "Close both", () -> {
            int p2 = TestControl.gamePid(this);
            if (p2 > 0) android.os.Process.killProcess(p2);
            finishAndRemoveTask();
        });
    }

    // ------------------------------------------------------------------ settings

    private static final String[][] GROUPS = {
        { "Frame rate", "fps" },
        { "Display", "resolution", "widescreen", "skip_intros", "crt_filter", "show_fps" },
        { "Game", "start", "boosters", "speed", "turbo_dialog" },
        { "Controls", "swap_confirm", "analog_run", "touch_controls", "rumble" },
        { "Screens", "bottom_screen", "bottom_buttons", "panel_dim", "swap_screens" },
        { "Sound", "menu_sounds", "music_volume", "sound_volume" },
    };

    /** A short note beside a choice, as FF7Recomp's frame-rate list has. */
    private static String choiceNote(String key, int i) {
        switch (key) {
            case "fps": return new String[] { "As shipped: fields 30, battles 15", "Memoria fills in frames: everything at 60", "Fields and battles at 120 (experimental, warmer)" }[i];
            case "start": return new String[] { "As shipped", "Straight back to the place you were last" }[i];
            case "resolution": return new String[] { "The screen's own", "Cooler, a little softer", "Coolest, for weaker devices", "Blocky: only when the GPU is what holds it back" }[i];
            case "touch_controls": return new String[] { "Only when no controller is connected", "Always on screen", "Never" }[i];
            default: return null;
        }
    }

    private View settingsPage() {
        LinearLayout col = column();
        // Memoria's own settings first: easy to find (the Memoria section at the end has it too)
        ViewGroup top = buttons();
        top.addView(button("Memoria settings: shaders, card game, battles, cheats...", v -> show("memoria")));
        col.addView(top);
        boolean first = true;
        for (String[] g : GROUPS) {
            TextView h = heading(g[0]);
            if (!first) h.setPadding(0, dp(14), 0, dp(4));
            first = false;
            col.addView(h);
            for (int k = 1; k < g.length; k++) {
                Settings.Option o = Settings.find(g[k]);
                if (o == null) continue;
                if (o.isBool()) {
                    CheckBox c = new CheckBox(this);
                    c.setText(withNote(o.label, o.note));
                    c.setChecked(Settings.get(this, o) != 0);
                    c.setOnCheckedChangeListener((x, on) -> { Settings.set(this, o, on ? 1 : 0); updateStatus(); });
                    col.addView(c);
                } else if (o.choices.length <= 3 || o.key.equals("resolution")) {
                    if (g.length > 2) col.addView(text(o.label, 15, ACCENT));
                    RadioGroup rg = new RadioGroup(this);
                    int cur = Settings.get(this, o);
                    for (int i = 0; i < o.choices.length; i++) {
                        RadioButton r = new RadioButton(this);
                        r.setId(View.generateViewId());
                        r.setText(withNote(o.choices[i], choiceNote(o.key, i)));
                        rg.addView(r);
                        if (i == cur) r.setChecked(true);
                        int val = i;
                        r.setOnCheckedChangeListener((x, on) -> { if (on) { Settings.set(this, o, val); updateStatus(); } });
                    }
                    col.addView(rg);
                } else {
                    col.addView(stepper(o));     // a longer scale (volumes, speed): left/right or A
                }
            }
        }
        TextView gh = heading("Guide");
        gh.setPadding(0, dp(14), 0, dp(4));
        col.addView(gh);
        col.addView(note("A walkthrough page of your choice on the second screen: Select+L1 switches between it and the panels; scroll it by touch. Leave empty for none."));
        android.widget.EditText url = new android.widget.EditText(this);
        url.setSingleLine(true);
        url.setHint("https://... (a guide page you like)");
        url.setText(Settings.prefs(this).getString("guide_url", ""));
        url.setTextColor(TEXT);
        url.setHintTextColor(DIM);
        url.setInputType(android.text.InputType.TYPE_CLASS_TEXT | android.text.InputType.TYPE_TEXT_VARIATION_URI);
        url.addTextChangedListener(new android.text.TextWatcher() {
            @Override public void beforeTextChanged(CharSequence c, int a, int b, int d) { }
            @Override public void onTextChanged(CharSequence c, int a, int b, int d) { }
            @Override public void afterTextChanged(android.text.Editable e) { Settings.prefs(LauncherActivity.this).edit().putString("guide_url", e.toString().trim()).apply(); }
        });
        col.addView(url);
        col.addView(space(8));
        ViewGroup r = buttons();
        r.addView(button("Arrange touch controls", v -> startActivity(new Intent(this, TouchLayoutActivity.class))));
        col.addView(r);
        TextView mh = heading("Memoria");
        mh.setPadding(0, dp(14), 0, dp(4));
        col.addView(mh);
        col.addView(note("Memoria's own settings, as its launcher on the PC has them: shaders, battles, the card game, the world map camera, cheats and more."));
        ViewGroup mr = buttons();
        mr.addView(button("Memoria settings", v -> show("memoria")));
        mr.addView(button("Edit Memoria.ini...", v -> editMemoriaIni()));
        col.addView(mr);
        return col;
    }

    // ------------------------------------------------------------------ Memoria's settings

    /** Memoria's own settings (MemoriaSettings), as switches and choices: each change is written
     *  into Memoria.ini at once, and the game reads it when it starts. */
    private View memoriaPage() {
        LinearLayout col = column();
        col.addView(heading("Memoria settings"));
        File ini = new File(install.game, "Memoria.ini");
        java.util.List<String> lines;
        try { lines = MemoriaSettings.read(ini); }
        catch (Exception e) { col.addView(text("Import the game first (Game files): Memoria's settings live in its Memoria.ini.", 16, DIM)); return col; }
        MemoriaSettings.userFonts = new File(install.files, "fonts");     // the player's own fonts (issue 36)
        MemoriaSettings.userFonts.mkdirs();
        col.addView(note("As Memoria's launcher on the PC has them. Changes take effect the next time the game starts. "
                + "Frame rate, widescreen, skip logos, boosters, fast dialog, button layout and volumes are in Settings."));
        for (String g : MemoriaSettings.GROUPS) {
            TextView h = heading(g);
            h.setPadding(0, dp(14), 0, dp(4));
            col.addView(h);
            if (g.equals(MemoriaSettings.CHEATS)) col.addView(note("The boosters here are the ones the in-game booster menu offers; Settings > Boosters turns that menu on."));
            for (MemoriaSettings.Opt o : MemoriaSettings.inGroup(g)) col.addView(memoriaRow(ini, o));
        }
        ViewGroup r = buttons();
        r.addView(button("Edit Memoria.ini...", v -> editMemoriaIni()));
        col.addView(space(8));
        col.addView(note("Everything else Memoria has, in the file itself:"));
        col.addView(r);
        return col;
    }

    private View memoriaRow(File ini, MemoriaSettings.Opt o) {
        int cur;
        try { cur = MemoriaSettings.current(MemoriaSettings.read(ini), o); } catch (Exception e) { cur = -1; }
        if (o.toggle) {
            CheckBox c = new CheckBox(this);
            c.setText(withNote(o.label, o.note));
            c.setChecked(cur == 1);
            c.setOnCheckedChangeListener((x, on) -> { if (!setMemoria(ini, o, on ? 1 : 0)) x.setChecked(!on); });
            return c;
        }
        int[] at = { cur };
        Button b = listRow("", null);
        Runnable label = () -> b.setText(withNote(o.label + ":   \u25C0 " + (at[0] >= 0 ? o.choices[at[0]].label : "Custom (set in Memoria.ini)") + " \u25B6", o.note));
        label.run();
        java.util.function.IntConsumer step = dir -> {
            int n = o.choices.length;
            int next = at[0] < 0 ? (dir > 0 ? 0 : n - 1) : (at[0] + dir + n) % n;
            if (setMemoria(ini, o, next)) { at[0] = next; label.run(); }
        };
        if (o.choices.length <= 6) b.setOnClickListener(v -> step.accept(1));
        else b.setOnClickListener(v -> {         // a long list (the fonts): pick from it
            String[] names = new String[o.choices.length];
            for (int i = 0; i < names.length; i++) names[i] = o.choices[i].label;
            styled(new AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
                    .setTitle(o.label)
                    .setSingleChoiceItems(names, at[0], (d, w) -> {
                        if (setMemoria(ini, o, w)) { at[0] = w; label.run(); }
                        d.dismiss();
                    })
                    .setNegativeButton("Cancel", null)
                    .show());
        });
        b.setOnKeyListener((v, code, e) -> {
            if (code != KeyEvent.KEYCODE_DPAD_LEFT && code != KeyEvent.KEYCODE_DPAD_RIGHT) return false;
            if (e.getAction() == KeyEvent.ACTION_DOWN) { Sfx.play(this, Sfx.CURSOR); step.accept(code == KeyEvent.KEYCODE_DPAD_RIGHT ? 1 : -1); }
            return true;
        });
        return b;
    }

    /** Writes one choice into Memoria.ini (read again first: the file may have changed). */
    private boolean setMemoria(File ini, MemoriaSettings.Opt o, int choice) {
        try {
            java.util.List<String> lines = MemoriaSettings.read(ini);
            MemoriaSettings.apply(lines, o.choices[choice]);
            MemoriaSettings.write(ini, lines);
            return true;
        } catch (Exception e) {
            message("Could not change Memoria.ini", e.getMessage());
            return false;
        }
    }

    /** Memoria.ini in a text box (GitHub issue 9): Save writes it back. */
    private void editMemoriaIni() {
        File ini = new File(install.game, "Memoria.ini");
        String text;
        try { text = new String(java.nio.file.Files.readAllBytes(ini.toPath()), java.nio.charset.StandardCharsets.ISO_8859_1); }
        catch (Exception e) { message("No Memoria.ini", "Import the game first (Game files)."); return; }
        android.widget.EditText ed = new android.widget.EditText(this);
        ed.setText(text.replace("\r\n", "\n"));
        ed.setTypeface(android.graphics.Typeface.MONOSPACE);
        ed.setTextSize(13);
        ed.setTextColor(TEXT);
        ed.setGravity(Gravity.TOP | Gravity.START);
        ed.setHorizontallyScrolling(true);
        ed.setInputType(android.text.InputType.TYPE_CLASS_TEXT | android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE
                | android.text.InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        android.widget.ScrollView sv = new android.widget.ScrollView(this);
        sv.addView(ed);
        AlertDialog d = styled(new AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
                .setTitle("Memoria.ini").setView(sv)
                .setPositiveButton("Save", (x, w) -> {
                    try {
                        Settings.writeIni(ini, ed.getText().toString().getBytes(java.nio.charset.StandardCharsets.ISO_8859_1));
                        status.setText("Memoria.ini saved.");
                    } catch (Exception e) { message("Could not write Memoria.ini", e.getMessage()); }
                })
                .setNegativeButton("Cancel", null).show());
        if (d.getWindow() != null) d.getWindow().setLayout(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT);
    }

    private View stepper(Settings.Option o) {
        Button b = listRow("", null);
        Runnable label = () -> b.setText(withNote(o.label + ":   ◀ " + Settings.valueText(this, o) + " ▶", o.note));
        label.run();
        b.setOnClickListener(v -> { change(o, 1); label.run(); });
        b.setOnKeyListener((v, code, e) -> {
            if (code != KeyEvent.KEYCODE_DPAD_LEFT && code != KeyEvent.KEYCODE_DPAD_RIGHT) return false;
            if (e.getAction() == KeyEvent.ACTION_DOWN) { change(o, code == KeyEvent.KEYCODE_DPAD_RIGHT ? 1 : -1); label.run(); }
            return true;
        });
        return b;
    }

    private void change(Settings.Option o, int dir) {
        int n = o.isBool() ? 2 : o.choices.length;
        Settings.set(this, o, (Settings.get(this, o) + dir + n) % n);
        updateStatus();
    }

    // ------------------------------------------------------------------ mods (installed)

    private String catQuery = "", catCategory;
    private boolean catInstalledOnly;
    private String modBack = "catalog";            // where B goes from a mod's page

    private View modsPage() {
        LinearLayout col = column();
        LinearLayout head = row();
        head.addView(heading("Mods"));
        col.addView(head);
        List<String> on = ModCatalog.enabled(this);
        List<String> folders = ModCatalog.installedFolders(this);
        col.addView(note("Memoria loads the mods that are on, top first: when two change the same thing, the higher one wins. "
                + "▲▼ change the order. Get mods finds more."));
        ViewGroup actions = buttons();
        actions.addView(button("Get mods...", v -> show("catalog")));
        if (!folders.isEmpty()) {
            actions.addView(button("All on", v -> allMods(true)));
            actions.addView(button("All off", v -> allMods(false)));
        }
        List<ModCatalog.Mod> ups = ModCatalog.updates(this);
        if (!ups.isEmpty()) actions.addView(button("Update all (" + ups.size() + ")", v -> queueMods(ups)));
        col.addView(actions);
        if (folders.isEmpty()) col.addView(text("No mods installed yet. Get mods lists Memoria's catalog.", 16, DIM));
        // the ones that are on, in load order, then the ones that are off
        List<String> ordered = new java.util.ArrayList<>();
        for (String f : on) if (folders.stream().anyMatch(f::equalsIgnoreCase)) ordered.add(f);
        for (String f : folders) if (ordered.stream().noneMatch(f::equalsIgnoreCase)) ordered.add(f);
        for (int i = 0; i < ordered.size(); i++) col.addView(installedRow(ordered.get(i), on, i));
        return col;
    }

    /** An installed mod: on/off, its place in the load order, Update, Remove, details. */
    private View installedRow(String folder, List<String> on, int index) {
        ModCatalog.Mod m = ModCatalog.byFolder(this, folder);
        // one copied in by hand: its own ModDescription.xml gives the name, options and Info (issue 15)
        ModCatalog.Mod shown = m != null ? m : ModCatalog.handMod(this, folder);
        boolean enabled = on.stream().anyMatch(folder::equalsIgnoreCase);
        int pos = -1;
        for (int i = 0; i < on.size(); i++) if (on.get(i).equalsIgnoreCase(folder)) pos = i;
        // the buttons wrap onto a second line if even their own line is too narrow
        ViewGroup r = new FlowRow(this);
        CheckBox cb = new CheckBox(this);
        String name = shown.name;
        String ver = m == null ? "added by hand" : ModCatalog.installedVersion(this, m).isEmpty() ? "" : "v" + ModCatalog.installedVersion(this, m);
        if (m != null && ModCatalog.updatable(this, m)) ver += (ver.isEmpty() ? "" : ", ") + "update " + m.version;
        int opts = ModCatalog.choiceCount(this, folder);
        if (opts > 0) ver += (ver.isEmpty() ? "" : ", ") + opts + " options under Info";
        cb.setText(withNote((enabled ? (pos + 1) + ".  " : "") + name, ver));
        cb.setChecked(enabled);
        cb.setTag("focus:on:" + folder);
        cb.setOnCheckedChangeListener((x, isOn) -> {
            try { ModCatalog.setEnabled(this, folder, isOn); } catch (Exception e) { message("Could not change Memoria.ini", e.getMessage()); }
            refresh();
        });
        if (enabled) {
            Button up = small("▲", v -> { moveMod(folder, -1); });
            up.setTag("focus:up:" + folder);
            up.setEnabled(pos > 0);
            Button dn = small("▼", v -> { moveMod(folder, 1); });
            dn.setTag("focus:down:" + folder);
            dn.setEnabled(pos < on.size() - 1);
            r.addView(up);
            r.addView(dn);
        }
        if (m != null && ModCatalog.updatable(this, m) && DownloadQueue.get(this).pending(m) == null)
            r.addView(small("Update", v -> queueMods(java.util.Collections.singletonList(m))));
        Button info = small("Info", v -> { modShown = shown; modBack = "mods"; show("mod"); });
        info.setTag("focus:info:" + folder);
        r.addView(info);
        Button rm = small("Remove", v -> confirmRemove(folder, m));
        rm.setTag("focus:rm:" + folder);
        r.addView(rm);
        // the name, then its buttons beside it; under it when they would leave the name too
        // little width (a 1240x1080 screen gave it a few letters per line, GitHub issue 30)
        View line = new NameAndButtons(this, cb, r, dp(200), dp(8));
        line.setTag("modrow:" + folder);
        return line;
    }

    private void moveMod(String folder, int dir) {
        try { ModCatalog.move(this, folder, dir); } catch (Exception e) { message("Could not change Memoria.ini", e.getMessage()); return; }
        refresh();
    }

    private void allMods(boolean on) {
        try { ModCatalog.setAll(this, on); } catch (Exception e) { message("Could not change Memoria.ini", e.getMessage()); return; }
        refresh();
    }

    private void confirmRemove(String folder, ModCatalog.Mod m) {
        if (TestControl.gamePid(this) > 0) { message("Leave the game first", "The game is using the mod's files. Close it with Select+Start, then remove the mod."); return; }
        ask("Remove " + (m != null ? m.name : folder) + "?", "Its folder (" + folder + ") is deleted from your game.", "Remove", () -> {
            try {
                if (m != null) ModDownloader.remove(this, m);
                else { ModCatalog.setEnabled(this, folder, false); Storage.deleteTree(new File(install.game, folder)); }
            } catch (Exception e) { message("Could not remove the mod", e.getMessage()); }
            refresh();
            focusContent();
        });
    }

    // ------------------------------------------------------------------ Get mods (the catalog)

    private LinearLayout catList;
    private final java.util.Map<String, LinearLayout> actionSlots = new HashMap<>();
    private final android.os.Handler ticker = new android.os.Handler(android.os.Looper.getMainLooper());
    private final Runnable searchNow = () -> {
        if (!"catalog".equals(page) || catList == null) return;
        fillCatalogList();
        theme(catList);
    };

    /** Get mods' list and categories, each in its own scroll view (refresh keeps where they were). */
    private ScrollView catScroll, catsScroll;

    private View catalogPage() {
        LinearLayout pageCol = column();
        List<ModCatalog.Mod> all = ModCatalog.mods(this);
        LinearLayout head = row();
        head.setGravity(Gravity.CENTER_VERTICAL);
        TextView h = heading("Get mods");
        h.setPadding(0, 0, dp(12), 0);
        head.addView(h);
        head.addView(text(all.size() + (all.size() == 1 ? " mod" : " mods"), 15, ACCENT));
        View gap = new View(this);
        head.addView(gap, new LinearLayout.LayoutParams(0, 1, 1));
        List<ModCatalog.Mod> ups = ModCatalog.updates(this);
        if (!ups.isEmpty()) head.addView(small("Update all (" + ups.size() + ")", v -> queueMods(ups)));
        Button rf = small("⟳ Refresh", v -> refreshCatalog(true));
        rf.setTag("focus:refresh");
        head.addView(rf);
        pageCol.addView(head);

        android.widget.EditText q = new android.widget.EditText(this);
        q.setHint("Search by name, author or word");
        q.setText(catQuery);
        q.setSingleLine(true);
        q.setTextSize(16);
        q.setTextColor(TEXT);
        q.setHintTextColor(DIM);
        q.setImeOptions(android.view.inputmethod.EditorInfo.IME_ACTION_SEARCH);
        q.setTag("focus:search");
        q.setSelection(catQuery.length());
        // the list follows what is typed: some handhelds' keyboards have no Enter key
        q.addTextChangedListener(new android.text.TextWatcher() {
            @Override public void beforeTextChanged(CharSequence c, int a, int b, int n) { }
            @Override public void onTextChanged(CharSequence c, int a, int b, int n) { }
            @Override public void afterTextChanged(android.text.Editable e) {
                catQuery = e.toString();
                ticker.removeCallbacks(searchNow);
                ticker.postDelayed(searchNow, 300);
            }
        });
        q.setOnEditorActionListener((v, id, ev) -> {
            catQuery = v.getText().toString();
            ticker.removeCallbacks(searchNow);
            searchNow.run();
            ((android.view.inputmethod.InputMethodManager) getSystemService(INPUT_METHOD_SERVICE)).hideSoftInputFromWindow(v.getWindowToken(), 0);
            return true;
        });
        pageCol.addView(q, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        LinearLayout split = new LinearLayout(this);
        split.setOrientation(LinearLayout.HORIZONTAL);
        split.setPadding(0, dp(6), 0, 0);
        LinearLayout cats = column();
        int installed = 0;
        java.util.Map<String, Integer> counts = new HashMap<>();
        for (ModCatalog.Mod m : all) {
            counts.merge(m.category.isEmpty() ? "Other" : m.category, 1, Integer::sum);
            if (ModCatalog.installed(this, m)) installed++;
        }
        cats.addView(category("All", all.size(), catCategory == null && !catInstalledOnly, v -> { catCategory = null; catInstalledOnly = false; refresh(); }));
        cats.addView(category("Installed", installed, catInstalledOnly, v -> { catCategory = null; catInstalledOnly = true; refresh(); }));
        View line = new View(this);
        line.setBackgroundColor(0x40FFFFFF);
        LinearLayout.LayoutParams llp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, Math.max(1, dp(1)));
        llp.setMargins(dp(8), dp(6), dp(8), dp(6));
        cats.addView(line, llp);
        for (String c : ModCatalog.categories(all))
            cats.addView(category(c, counts.getOrDefault(c, 0), c.equals(catCategory) && !catInstalledOnly, v -> { catCategory = c; catInstalledOnly = false; refresh(); }));
        // the categories and the list scroll each on their own: the list moves, the categories stay
        catsScroll = new ScrollView(this);
        catsScroll.setVerticalScrollBarEnabled(false);
        catsScroll.addView(cats);
        split.addView(catsScroll, new LinearLayout.LayoutParams(dp(smallScreen ? 104 : 140), ViewGroup.LayoutParams.MATCH_PARENT));
        View vline = new View(this);
        vline.setBackgroundColor(0x40FFFFFF);
        LinearLayout.LayoutParams vlp = new LinearLayout.LayoutParams(Math.max(1, dp(1)), ViewGroup.LayoutParams.MATCH_PARENT);
        vlp.setMargins(dp(smallScreen ? 4 : 8), 0, dp(smallScreen ? 6 : 12), 0);
        split.addView(vline, vlp);
        catList = column();
        fillCatalogList();
        catScroll = new ScrollView(this);
        catScroll.addView(catList);
        split.addView(catScroll, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1));
        pageCol.addView(split, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1));
        return pageCol;
    }

    private void fillCatalogList() {
        LinearLayout list = catList;
        list.removeAllViews();
        actionSlots.clear();
        List<ModCatalog.Mod> all = ModCatalog.mods(this);
        if (all.isEmpty()) {
            list.addView(text("No catalog yet. Check the internet connection and press Refresh.", 16, DIM));
            return;
        }
        List<ModCatalog.Mod> found = ModCatalog.search(this, all, catQuery, catCategory, catInstalledOnly);
        if (found.isEmpty()) list.addView(text("Nothing matches.", 16, DIM));
        for (int i = 0; i < found.size(); i++) {
            if (i > 0) {
                View d = new View(this);
                d.setBackgroundColor(0x30FFFFFF);
                list.addView(d, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, Math.max(1, dp(1))));
            }
            list.addView(catalogRow(found.get(i), catCategory == null));
        }
    }

    /** A category in Get mods: its name and count; the one shown is lit. */
    private Button category(String label, int count, boolean on, View.OnClickListener l) {
        Button b = new Button(this);
        android.text.SpannableString t = new android.text.SpannableString(label + "  " + count);
        t.setSpan(new ForegroundColorSpan(on ? TEXT : DIM), label.length(), t.length(), 0);
        t.setSpan(new RelativeSizeSpan(0.8f), label.length(), t.length(), 0);
        b.setText(t);
        b.setAllCaps(false);
        b.setTextSize(14);
        b.setTextColor(on ? ACCENT : TEXT);
        shadow(b);
        b.setSingleLine(true);
        b.setEllipsize(android.text.TextUtils.TruncateAt.END);
        b.setGravity(Gravity.CENTER_VERTICAL | Gravity.START);
        b.setPadding(dp(8), 0, dp(4), 0);
        b.setMinHeight(0);
        b.setMinimumHeight(0);
        b.setStateListAnimator(null);
        StateListDrawable sl = new StateListDrawable();
        sl.addState(new int[] { android.R.attr.state_focused }, window(HOT_TOP, HOT_BOTTOM, 6));
        sl.addState(new int[] { android.R.attr.state_pressed }, window(HOT_TOP, HOT_BOTTOM, 6));
        if (on) sl.addState(new int[] {}, window(BTN_TOP, BTN_BOTTOM, 6));
        b.setBackground(sl);
        b.setOnClickListener(l);
        b.setTag("focus:cat:" + label);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(34));
        lp.setMargins(0, dp(1), 0, dp(1));
        b.setLayoutParams(lp);
        return b;
    }

    private View catalogRow(ModCatalog.Mod m, boolean showCategory) {
        LinearLayout info = column();
        info.setPadding(dp(8), dp(6), dp(8), dp(6));
        TextView name = text(m.name, 16, TEXT);
        name.setTypeface(Typeface.DEFAULT_BOLD);
        name.setMaxLines(2);
        name.setEllipsize(android.text.TextUtils.TruncateAt.END);
        info.addView(name);
        StringBuilder b = new StringBuilder();
        if (!m.author.isEmpty()) b.append(m.author);
        if (showCategory && !m.category.isEmpty()) b.append(b.length() > 0 ? "  ·  " : "").append(m.category);
        if (!m.version.isEmpty()) b.append(b.length() > 0 ? "  ·  " : "").append("v").append(m.version);
        if (!m.sizeText().isEmpty()) b.append(b.length() > 0 ? "  ·  " : "").append(m.sizeText());
        TextView meta = text(b.toString(), 13, DIM);
        meta.setSingleLine(true);
        meta.setEllipsize(android.text.TextUtils.TruncateAt.END);
        info.addView(meta);
        info.setFocusable(true);
        info.setClickable(true);
        info.setTag("focus:mod:" + m.folder);
        info.setOnClickListener(v -> { modShown = m; modBack = "catalog"; show("mod"); });
        GradientDrawable lit = new GradientDrawable();
        lit.setColor(0x33FFFFFF);
        lit.setCornerRadius(dp(6));
        StateListDrawable sl = new StateListDrawable();
        sl.addState(new int[] { android.R.attr.state_focused }, lit);
        sl.addState(new int[] { android.R.attr.state_pressed }, lit);
        info.setBackground(sl);
        LinearLayout slot = new LinearLayout(this);
        slot.setGravity(Gravity.CENTER_VERTICAL | Gravity.END);
        fillAction(slot, m);
        actionSlots.put(m.folder, slot);
        LinearLayout r = new LinearLayout(this);
        r.setOrientation(LinearLayout.HORIZONTAL);
        r.setGravity(Gravity.CENTER_VERTICAL);
        r.setPadding(0, dp(4), 0, dp(4));
        r.addView(info, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        r.addView(slot, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        return r;
    }

    /** A Get mods row's action: Install / Update / Installed, or its place in the queue. */
    private void fillAction(LinearLayout slot, ModCatalog.Mod m) {
        DownloadQueue.Item q = DownloadQueue.get(this).pending(m);
        boolean inst = ModCatalog.installed(this, m), up = ModCatalog.updatable(this, m);
        String label = q != null ? queueLabel(q, false) : !inst ? "\u0001install" : up ? "\u0001update" : "\u0001installed";
        if (label.equals(slot.getTag())) return;                     // unchanged: keep the focus
        boolean hadFocus = slot.hasFocus();
        slot.setTag(label);
        slot.removeAllViews();
        Button b;
        if (q != null) b = small(label, v -> show("downloads"));
        else if (!inst) b = small("Install", v -> queueMods(java.util.Collections.singletonList(m)));
        else if (up) b = small("Update", v -> queueMods(java.util.Collections.singletonList(m)));
        else { slot.addView(text("✓ Installed", 14, ACCENT)); return; }
        b.setTag("focus:act:" + m.folder);
        slot.addView(b);
        if (hadFocus) b.requestFocus();
    }

    /** "Queued", "Downloading 40%", "Unpacking"; long: with sizes. */
    private String queueLabel(DownloadQueue.Item q, boolean longForm) {
        switch (q.state) {
            case QUEUED: return DownloadQueue.get(this).paused() ? "Paused" : "Queued";
            case RUNNING:
                int pc = q.percent();
                if (!"Downloading".equals(q.phase)) return q.phase + (pc >= 0 && longForm ? " " + pc + "%" : "...");
                if (!longForm) return pc >= 0 ? "Downloading " + pc + "%" : "Downloading...";
                return "Downloading " + Storage.human(q.got) + (q.total > 0 ? " of " + Storage.human(q.total) : "") + (pc >= 0 ? "  (" + pc + "%)" : "");
            case DONE: return q.update ? "Updated to " + q.mod.version : "Installed and turned on";
            case CANCELLED: return "Cancelled";
            default: return "Failed: " + q.error;
        }
    }

    /** Queues mods for download; the game must not be running while one is replaced. */
    private void queueMods(List<ModCatalog.Mod> mods) {
        int n = 0;
        StringBuilder clash = new StringBuilder();
        for (ModCatalog.Mod m : mods) {
            if (DownloadQueue.get(this).add(m, ModCatalog.installed(this, m))) n++;
            for (String other : m.incompatible) {
                ModCatalog.Mod o = ModCatalog.byFolder(this, other);
                String on = o != null ? o.folder : other;
                if (ModCatalog.enabled(this).stream().anyMatch(on::equalsIgnoreCase))
                    clash.append("\n· ").append(m.name).append(" does not work with ").append(o != null ? o.name : other).append(", which is on.");
            }
        }
        updateStatus();
        refresh();
        if (clash.length() > 0) message("Check these mods", "Memoria's catalog says:" + clash + "\n\nTurn one of them off under Mods once the download is done.");
        else if (n > 0) status.setText(n == 1 ? "Downloading " + mods.get(0).name + " (Downloads shows the progress)." : n + " mods queued (Downloads shows the progress).");
    }

    // ------------------------------------------------------------------ a mod's page

    private View modPage(ModCatalog.Mod m) {
        LinearLayout col = column();
        if (m == null) return col;
        col.addView(heading(m.name));
        StringBuilder meta = new StringBuilder();
        if (!m.author.isEmpty()) meta.append("by ").append(m.author);
        if (!m.category.isEmpty()) meta.append(meta.length() > 0 ? "  ·  " : "").append(m.category);
        if (!m.version.isEmpty()) meta.append(meta.length() > 0 ? "  ·  " : "").append("version ").append(m.version);
        if (!m.releaseDate.isEmpty()) meta.append("  ·  released ").append(m.releaseDate);
        if (!m.sizeText().isEmpty()) meta.append("  ·  ").append(m.sizeText());
        col.addView(text(meta.toString(), 14, ACCENT));
        boolean inst = ModCatalog.installed(this, m), on = inst && ModCatalog.isEnabled(this, m);
        String iv = ModCatalog.installedVersion(this, m);
        col.addView(note(inst ? "Installed" + (iv.isEmpty() ? "" : ": version " + iv) + (on ? ", on" : ", off") + "  ·  folder " + m.folder
                : "Not installed  ·  goes into the folder " + m.folder));
        for (String other : m.incompatible) {
            ModCatalog.Mod o = ModCatalog.byFolder(this, other);
            col.addView(text("Does not work with " + (o != null ? o.name : other) + ".", 14, 0xFFFF9A80));
        }
        if (!m.minMemoria.isEmpty() && m.minMemoria.replaceAll("\\D", "").compareTo(MemoriaInstall.VERSION.replaceAll("\\D", "")) > 0) col.addView(note("Needs Memoria " + m.minMemoria + " or later (the app installs " + MemoriaInstall.VERSION + ")."));
        ViewGroup actions = buttons();
        DownloadQueue.Item q = DownloadQueue.get(this).pending(m);
        if (q != null) actions.addView(button(queueLabel(q, false), v -> show("downloads")));
        else if (!inst) actions.addView(button("Install", v -> queueMods(java.util.Collections.singletonList(m))));
        else {
            actions.addView(button(on ? "Turn off" : "Turn on", v -> {
                try { ModCatalog.setEnabled(this, m, !on); } catch (Exception e) { message("Could not change Memoria.ini", e.getMessage()); return; }
                refresh();
            }));
            if (ModCatalog.updatable(this, m)) actions.addView(button("Update to " + m.version, v -> queueMods(java.util.Collections.singletonList(m))));
            actions.addView(button("Remove", v -> confirmRemove(m.folder, m)));
        }
        if (!m.website.isEmpty()) actions.addView(button("Mod's web page", v -> {
            try { startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(m.website))); }
            catch (Exception e) { message("No browser", "Nothing on this device can open " + m.website); }
        }));
        actions.addView(button("Back", v -> show(modBack)));
        col.addView(actions);
        if (inst && ModCatalog.choiceCount(this, m.folder) > 0) col.addView(modOptions(m.folder));
        if (!m.preview.isEmpty()) {
            android.widget.ImageView img = new android.widget.ImageView(this);
            img.setAdjustViewBounds(true);
            img.setScaleType(android.widget.ImageView.ScaleType.FIT_START);
            img.setMaxHeight(dp(220));
            LinearLayout.LayoutParams ip = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            ip.setMargins(0, dp(8), 0, dp(4));
            col.addView(img, ip);
            loadPreview(m, img);
        }
        TextView d = text(m.description.replaceAll("[ \\t]*\\n[ \\t]*", "\n"), 14, TEXT);
        d.setPadding(0, dp(8), 0, 0);
        col.addView(d);
        if (!m.patchNotes.isEmpty()) {
            TextView ph = text("Release notes", 15, ACCENT);
            ph.setPadding(0, dp(10), 0, 0);
            col.addView(ph);
            col.addView(text(m.patchNotes.replaceAll("[ \\t]*\\n[ \\t]*", "\n"), 14, DIM));
        }
        return col;
    }

    /** An installed mod's options (Memoria's sub-mods), as Memoria's Mod Manager lists them: under
     *  their headers, one checkbox each; one of a group replaces the others. */
    private View modOptions(String folder) {
        LinearLayout box = column();
        TextView h = text("Options", 15, ACCENT);
        h.setPadding(0, dp(10), 0, 0);
        box.addView(h);
        box.addView(note("The mod's own choices, as in Memoria's Mod Manager. Choosing one of a group replaces the others in it. "
                + "Changes apply the next time the game starts."));
        java.util.Set<String> chosen = ModCatalog.chosen(this, folder);
        String header = null;
        for (ModCatalog.SubMod s : ModCatalog.subMods(this, folder)) {
            if (s.header) { header = s.name; continue; }
            if (!s.choice()) continue;
            if (header != null) {
                TextView t = text(header, 14, TEXT);
                t.setTypeface(Typeface.DEFAULT_BOLD);
                t.setPadding(0, dp(8), 0, dp(2));
                box.addView(t);
                header = null;
            }
            CheckBox cb = new CheckBox(this);
            cb.setText(withNote(s.name, s.description.replaceAll("\\s*\\n\\s*", " ").trim()));
            cb.setChecked(ModCatalog.isChosen(chosen, s));
            cb.setTag("focus:opt:" + folder + "/" + s.path);
            cb.setOnCheckedChangeListener((x, on) -> {
                try { ModCatalog.choose(this, folder, s, on); } catch (Exception e) { message("Could not change Memoria.ini", e.getMessage()); }
                refresh();
            });
            box.addView(cb);
        }
        return box;
    }

    /** The mod's preview picture, downloaded once into files/mods/previews. */
    private void loadPreview(ModCatalog.Mod m, android.widget.ImageView into) {
        File f = new File(new File(ModCatalog.dir(this), "previews"), m.folder.replaceAll("[^A-Za-z0-9._-]", "_") + ".img");
        new Thread(() -> {
            try {
                if (!f.isFile() || f.length() == 0) {
                    f.getParentFile().mkdirs();
                    java.net.HttpURLConnection h = (java.net.HttpURLConnection) new java.net.URL(m.preview).openConnection();
                    h.setRequestProperty("User-Agent", "FF9Recomp");
                    h.setInstanceFollowRedirects(true);
                    h.setConnectTimeout(15000);
                    h.setReadTimeout(30000);
                    if (h.getResponseCode() != 200) return;
                    File tmp = new File(f.getPath() + ".tmp");
                    try (java.io.InputStream in = h.getInputStream(); java.io.OutputStream o = new java.io.FileOutputStream(tmp)) {
                        byte[] b = new byte[1 << 16];
                        for (int n; (n = in.read(b)) > 0; ) o.write(b, 0, n);
                    }
                    if (!tmp.renameTo(f)) return;
                }
                android.graphics.BitmapFactory.Options o = new android.graphics.BitmapFactory.Options();
                o.inJustDecodeBounds = true;
                android.graphics.BitmapFactory.decodeFile(f.getPath(), o);
                o.inSampleSize = 1;
                while (o.outWidth / (o.inSampleSize * 2) >= 900) o.inSampleSize *= 2;
                o.inJustDecodeBounds = false;
                android.graphics.Bitmap bm = android.graphics.BitmapFactory.decodeFile(f.getPath(), o);
                if (bm != null) runOnUiThread(() -> into.setImageBitmap(bm));
            } catch (Exception ignored) { }
        }, "mod-preview").start();
    }

    // ------------------------------------------------------------------ Downloads (the queue)

    private final java.util.Map<DownloadQueue.Item, Object[]> progressViews = new HashMap<>();
    private String queueSig = "";

    private View downloadsPage() {
        LinearLayout col = column();
        col.addView(heading("Downloads"));
        col.addView(note("Mods download one at a time, top first, while the launcher stays usable. A mod being replaced waits for the game to close. "
                + "They go on with the launcher in the background (the notification has Pause). "
                + "If Android stops the app, the queue carries on, and the download resumes, when the launcher opens again."));
        ViewGroup bar = buttons();
        bar.addView(button("Get mods...", v -> show("catalog")));
        bar.addView(button("Clear finished", v -> { DownloadQueue.get(this).clearFinished(); refresh(); }));
        DownloadQueue dq = DownloadQueue.get(this);
        boolean held = dq.paused() && dq.pendingCount() > 0;
        if (held) bar.addView(button("Continue", v -> { askNotifications(); dq.resume(); refresh(); }));
        else if (dq.pendingCount() > 0) bar.addView(button("Pause", v -> { dq.pause(); refresh(); }));
        col.addView(bar);
        if (held) col.addView(text("Paused. Continue goes on where the downloads stopped.", 14, ACCENT));
        progressViews.clear();
        List<DownloadQueue.Item> items = DownloadQueue.get(this).snapshot();
        if (items.isEmpty()) col.addView(text("Nothing downloading. Pick mods under Get mods.", 16, DIM));
        for (DownloadQueue.Item q : items) col.addView(downloadRow(q));
        return col;
    }

    private View downloadRow(DownloadQueue.Item q) {
        LinearLayout whole = column();
        whole.setPadding(0, dp(8), 0, dp(8));
        LinearLayout top = new LinearLayout(this);
        top.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout info = column();
        TextView name = text(q.mod.name + (q.update ? "  (update to " + q.mod.version + ")" : ""), 17, TEXT);
        name.setTypeface(Typeface.DEFAULT_BOLD);
        info.addView(name);
        boolean bad = q.state == DownloadQueue.State.FAILED;
        TextView line = text(queueLabel(q, true) + (q.state == DownloadQueue.State.QUEUED && !q.mod.sizeText().isEmpty() ? "  ·  " + q.mod.sizeText() : ""),
                14, bad ? 0xFFFF9A80 : q.state == DownloadQueue.State.DONE ? ACCENT : DIM);
        info.addView(line);
        top.addView(info, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        DownloadQueue dq = DownloadQueue.get(this);
        switch (q.state) {
            case QUEUED:
                top.addView(small("▲", v -> dq.move(q, -1)));
                top.addView(small("▼", v -> dq.move(q, 1)));
                top.addView(small("Cancel", v -> dq.cancel(q)));
                break;
            case RUNNING:
                top.addView(small("Cancel", v -> dq.cancel(q)));
                break;
            case FAILED: case CANCELLED:
                top.addView(small("Retry", v -> dq.retry(q)));
                break;
            default:
                break;
        }
        whole.addView(top);
        android.widget.ProgressBar pb = null;
        if (q.state == DownloadQueue.State.RUNNING) {
            pb = new android.widget.ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
            pb.setMax(100);
            pb.setProgressTintList(android.content.res.ColorStateList.valueOf(ACCENT));
            int pc = q.percent();
            pb.setIndeterminate(pc < 0);
            if (pc >= 0) pb.setProgress(pc);
            whole.addView(pb, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(8)));
        }
        progressViews.put(q, new Object[] { line, pb });
        return whole;
    }

    private String queueSignature() {
        StringBuilder b = new StringBuilder();
        for (DownloadQueue.Item q : DownloadQueue.get(this).snapshot()) b.append(q.mod.folder).append(':').append(q.state).append(';');
        return b.toString();
    }

    /** The queue moved: the status line always; the page in place when only progress changed. */
    private void queueChanged() {
        String sig = queueSignature();
        boolean structural = !sig.equals(queueSig);
        queueSig = sig;
        updateStatus();
        updateDownloadsLabel();
        switch (page) {
            case "downloads":
                if (structural) { refresh(); return; }
                for (java.util.Map.Entry<DownloadQueue.Item, Object[]> e : progressViews.entrySet()) {
                    DownloadQueue.Item q = e.getKey();
                    ((TextView) e.getValue()[0]).setText(queueLabel(q, true));
                    android.widget.ProgressBar pb = (android.widget.ProgressBar) e.getValue()[1];
                    if (pb != null && q.percent() >= 0) { pb.setIndeterminate(false); pb.setProgress(q.percent()); }
                }
                break;
            case "catalog":
                List<ModCatalog.Mod> all = ModCatalog.mods(this);
                for (ModCatalog.Mod m : all) { LinearLayout slot = actionSlots.get(m.folder); if (slot != null) { fillAction(slot, m); theme(slot); } }
                break;
            case "mods": case "mod":
                if (structural) refresh();
                break;
            default:
                break;
        }
    }

    private void updateDownloadsLabel() {
        Button b = nav.get("downloads");
        if (b == null) return;
        int n = DownloadQueue.get(this).pendingCount();
        b.setText(n > 0 ? "Downloads (" + n + ")" : "Downloads");
    }

    /** The catalog from Memoria's GitHub: on demand (loud), or quietly when it is a day old. */
    private void refreshCatalog(boolean loud) {
        if (loud) status.setText("Getting Memoria's catalog...");
        new Thread(() -> {
            String m = null;
            try { ModCatalog.refresh(this); } catch (Exception e) { m = "Could not get the catalog: " + e.getMessage(); }
            String mm = m;
            runOnUiThread(() -> {
                if (page.equals("catalog") || page.equals("mods")) refresh();
                updateStatus();
                if (mm != null && loud) message("Mod catalog", mm);
                else if (loud) status.setText("The catalog is up to date: " + ModCatalog.mods(this).size() + " mods.");
            });
        }, "catalog").start();
    }

    // ------------------------------------------------------------------ save states

    private View statesPage() {
        LinearLayout col = column();
        col.addView(heading("Save states"));
        col.addView(note("In game, Select+R1 keeps the place the party is in. \"Play from here\" starts the game there (at the start of that place); Y or Delete removes a slot."));
        for (int i = 1; i <= SaveStates.SLOTS; i++) {
            int slot = i;
            String d = SaveStates.describe(this, slot);
            LinearLayout r = new LinearLayout(this);
            r.setGravity(Gravity.CENTER_VERTICAL);
            Button b = listRow(withNote("Slot " + slot, d == null ? "empty" : d), v -> resume(slot));
            b.setTag(slot);
            r.addView(b, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
            if (d != null) {
                Button play = small("Play from here", v -> resume(slot));
                play.setTag("focus:play:" + slot);
                r.addView(play);
                Button del = small("Delete", v -> deleteState(slot));
                del.setTag("focus:del:" + slot);
                r.addView(del);
            }
            col.addView(r);
        }
        return col;
    }

    private void deleteState(int slot) {
        if (!SaveStates.used(this, slot)) return;
        ask("Delete slot " + slot + "?", SaveStates.describe(this, slot) + "\n\nThis can't be undone.", "Delete", () -> {
            SaveStates.delete(this, slot);
            refresh();
        });
    }

    private void resume(int slot) {
        if (!SaveStates.used(this, slot)) return;
        if (TestControl.gamePid(this) > 0) { status.setText("Leave the game first (Select+Start)."); return; }
        try {
            SaveStates.restore(this, slot);
        } catch (Exception e) {
            status.setText("Could not resume: " + e.getMessage());
            return;
        }
        play(true);
    }

    // ------------------------------------------------------------------ achievements

    private View achievementsPage() {
        LinearLayout col = column();
        col.addView(heading("Achievements"));
        List<String> got = Achievements.unlocked(this);
        col.addView(text(got.size() + " unlocked on this device", 16, ACCENT));
        SteamLink st = new SteamLink(this);
        col.addView(note(st.signedIn()
                ? "Signed in to Steam as " + st.account() + ". " + (st.waiting() > 0 ? st.waiting() + " waiting to be sent." : "All sent.")
                : "Sign in to send them to your Steam account: scan a QR code with the Steam app. You never type your Steam password here."));
        ViewGroup actions = buttons();
        if (!st.signedIn()) actions.addView(button("Sign in to Steam", v -> steamSignIn()));
        else {
            actions.addView(button("Send to Steam now", v -> steamSync()));
            actions.addView(button("Sign out", v -> ask("Sign out of Steam?", "Achievements earned while signed out wait here and go to Steam when you sign in again.", "Sign out",
                    () -> { st.signOut(); refresh(); focusContent(); })));
        }
        col.addView(actions);
        // the whole list from Steam's schema (signed in), else the ones earned here
        List<String[]> all = steamAchievements(st);
        if (!all.isEmpty()) {
            int have = 0;
            for (String[] a : all) if (a[1].equals("1") || got.contains(a[0])) have++;
            col.addView(text(have + " of " + all.size() + " on your Steam account", 15, ACCENT));
            for (String[] a : all) {
                boolean on = a[1].equals("1") || got.contains(a[0]);
                col.addView(text((on ? "★  " : "☆  ") + a[2], 15, on ? TEXT : DIM));
            }
        } else for (String id : got) col.addView(text("★  " + Achievements.pretty(id), 15, TEXT));
        if (all.isEmpty() && got.isEmpty()) col.addView(text("None yet: they unlock as you play.", 15, DIM));
        return col;
    }

    /** {api name, "0"/"1", display name} from Steam's state (files/steam/state.txt), when signed in. */
    private List<String[]> steamAchievements(SteamLink st) {
        List<String[]> out = new java.util.ArrayList<>();
        if (!st.signedIn() || !st.state.isFile()) return out;
        try {
            for (String l : java.nio.file.Files.readAllLines(st.state.toPath(), java.nio.charset.StandardCharsets.UTF_8)) {
                String[] f = l.split(" ", 4);
                if (f.length >= 4 && f[0].equals("ach")) out.add(new String[] { f[1], f[2], f[3] });
            }
        } catch (Exception ignored) { }
        return out;
    }

    private void steamSync() {
        busy = true;
        status.setText("Talking to Steam...");
        new Thread(() -> {
            String m;
            try {
                List<String> got = new SteamLink(this).sync();
                m = got.isEmpty() ? "Steam is up to date." : "Sent to Steam: " + String.join(", ", got);
            } catch (Exception e) {
                m = "Steam: " + SteamLink.message(e);
            }
            String mm = m;
            runOnUiThread(() -> { busy = false; refresh(); focusContent(); status.setText(mm); });
        }).start();
    }

    private void steamSignIn() {
        LinearLayout box = column();
        box.setGravity(Gravity.CENTER_HORIZONTAL);
        box.setPadding(dp(16), dp(12), dp(16), dp(4));
        box.addView(text("On your phone: open the Steam app, tap the Steam Guard shield, then \"Scan a QR code\", and approve the sign-in.", 15, TEXT));
        android.widget.ImageView qr = new android.widget.ImageView(this);
        qr.setBackgroundColor(Color.WHITE);
        android.util.DisplayMetrics dm = getResources().getDisplayMetrics();
        int size = Math.max(dp(150), Math.min(dp(260), Math.min(dm.widthPixels, dm.heightPixels) - dp(220)));
        box.addView(qr, new LinearLayout.LayoutParams(size, size));
        TextView st = text("Connecting to Steam...", 13, DIM);
        box.addView(st);
        ScrollView sv = new ScrollView(this);
        sv.addView(box);
        final java.util.concurrent.Future<?>[] cancel = new java.util.concurrent.Future<?>[1];
        final boolean[] open = { true };
        AlertDialog dlg = styled(new AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
                .setTitle("Sign in to Steam").setView(sv).setNegativeButton("Cancel", null).show());
        dlg.setOnDismissListener(d -> { open[0] = false; if (cancel[0] != null) cancel[0].cancel(true); });
        SteamLink link = new SteamLink(this);
        new Thread(() -> link.signInQr(new SteamLink.QrListener() {
            @Override public void qr(String url) {
                android.graphics.Bitmap bm = qrBitmap(url, size);
                runOnUiThread(() -> { qr.setImageBitmap(bm); st.setText("Waiting for the Steam app..."); });
            }
            @Override public void done(String error) {
                runOnUiThread(() -> {
                    if (open[0]) dlg.dismiss();
                    refresh();
                    if (error == null) status.setText("Signed in to Steam as " + link.account() + ".");
                    else if (!error.equals("cancelled")) status.setText("Not signed in: " + error);
                });
            }
        }, cancel), "steam-sign-in").start();
    }

    private static android.graphics.Bitmap qrBitmap(String text, int size) {
        try {
            com.google.zxing.common.BitMatrix m = new com.google.zxing.qrcode.QRCodeWriter()
                    .encode(text, com.google.zxing.BarcodeFormat.QR_CODE, size, size);
            int[] px = new int[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++) px[y * size + x] = m.get(x, y) ? Color.BLACK : Color.WHITE;
            return android.graphics.Bitmap.createBitmap(px, size, size, android.graphics.Bitmap.Config.ARGB_8888);
        } catch (Exception e) {
            return null;
        }
    }

    // ------------------------------------------------------------------ game files

    private View gamePage() {
        LinearLayout col = column();
        col.addView(heading("Game files"));
        View job = jobSection();       // before "imported": a paused import over an old copy is not done
        if (job != null) col.addView(job);
        boolean has = install.hasGame();
        String missing = has ? install.missingMessage() : null;
        col.addView(text(!has ? "No game imported yet." : missing != null ? missing
                : "Your FINAL FANTASY IX is imported" + (install.hasMoguri() ? ", with Moguri Mod." : "."), 15, has && missing == null ? ACCENT : TEXT));
        col.addView(note("Pick your FINAL FANTASY IX folder (the Steam copy, with Memoria installed; Moguri Mod if you use it). It is copied into the app once; nothing from the game ships in the app."));
        ViewGroup actions = buttons();
        actions.addView(button(has ? "Import again" : "Import game folder", v -> pickGame()));
        if (has && install.needsMemoria()) actions.addView(button("Install Memoria", v -> installMemoria()));
        col.addView(actions);
        if (has && install.needsMemoria())
            col.addView(note("Install Memoria downloads Memoria " + MemoriaInstall.VERSION + " from its GitHub page (about 90 MB) and installs it into this copy, "
                    + "as its installer does on the PC. Your saves, mods and Memoria settings stay."));

        TextView hd = heading("Download from Steam");
        hd.setPadding(0, dp(14), 0, dp(4));
        col.addView(hd);
        col.addView(note("No PC needed: sign in to Steam with a QR code and the app downloads FINAL FANTASY IX (about 8 GB, every file checked against Steam's), "
                + "then installs Memoria " + MemoriaInstall.VERSION + " from its GitHub page. A stopped download goes on where it was; "
                + (has ? "Repair downloads only the files that are missing or damaged, and keeps your mods, saves and Memoria settings." : "Moguri Mod can be added from Mods afterwards.")));
        ViewGroup dl = buttons();
        dl.addView(button(has ? "Repair from Steam" : "Download from Steam", v -> steamDownload()));
        col.addView(dl);

        TextView hs = heading("Saves");
        hs.setPadding(0, dp(14), 0, dp(4));
        col.addView(hs);
        col.addView(note("Bring your PC saves: in the PC's AppData/LocalLow/SquareEnix/FINAL FANTASY IX/Steam/EncryptedSavedData, pick all the files at once "
                + "(SavedData_ww.dat and Memoria's SavedData_ww_Memoria_... files), or a zip of them. The saves here are put aside as a backup first. "
                + "Export puts every save here in one zip, to keep, or to bring back later with Import."));
        ViewGroup sv = buttons();
        sv.addView(button("Import saves", v -> startActivityForResult(new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*")
                .putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true), PICK_SAVE)));
        sv.addView(button("Export saves", v -> startActivityForResult(new Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("application/zip")
                .putExtra(Intent.EXTRA_TITLE, "FF9Recomp-saves-" + new java.text.SimpleDateFormat("yyyyMMdd-HHmm", java.util.Locale.US).format(new java.util.Date()) + ".zip"), EXPORT_SAVES)));
        col.addView(sv);
        cloudSavesSection(col);

        TextView h = heading("Storage");
        h.setPadding(0, dp(14), 0, dp(4));
        col.addView(h);
        col.addView(note("The game, mods and saves are " + (Storage.onSd(this) ? "on the SD card" + (Storage.sdMissing(this) ? " (not inserted)" : "") : "in internal storage")
                + ", " + Storage.human(install.files.getUsableSpace()) + " free there. Moving copies everything, then removes the old copy."));
        ViewGroup st = buttons();
        st.addView(button(Storage.onSd(this) ? "Move to internal storage" : "Move to the SD card", v -> moveStorage()));
        col.addView(st);

        h = heading("App");
        h.setPadding(0, dp(14), 0, dp(4));
        col.addView(h);
        Updater.Release up = Updater.available;
        col.addView(note("FF9Recomp " + Reports.version(this) + (up != null ? ". Version " + up.version + " is out: your imported game, mods and saves are kept." : ".")));
        ViewGroup ap = buttons();
        ap.addView(button(up != null ? "Update to " + up.version : "Check for updates", v -> updateOrCheck()));
        col.addView(ap);
        return col;
    }

    /** Game files > Saves: Steam Cloud saves. Enables syncing with Steam Cloud via the game's Cloud menu on the title screen. */
    private void cloudSavesSection(LinearLayout col) {
        SteamLink st = new SteamLink(this);
        android.content.SharedPreferences p = Settings.prefs(this);
        boolean on = st.signedIn() && Settings.cloudSaves(this);
        CheckBox cb = new CheckBox(this);
        cb.setText(withNote("Sync with Steam Cloud", st.signedIn() ? "signed in as " + st.account() : "needs the Steam sign-in"));
        cb.setChecked(on);
        cb.setOnCheckedChangeListener((x, want) -> {
            if (want && !new SteamLink(this).signedIn()) {
                x.setChecked(false);
                status.setText("Sign in to Steam first, then turn Steam Cloud saves on.");
                steamSignIn();
                return;
            }
            p.edit().putBoolean(Settings.CLOUD_SAVES, want).apply();
            if (want) syncCloud(null);
            else refresh();
        });
        col.addView(cb);
        col.addView(note("Turns on the Cloud Data menu on the game's title screen, to upload and download your saves with Steam Cloud. "
        + "An upload reaches Steam when you leave the game."));
        if (on) {
            String last = p.getString(Settings.CLOUD_LAST, null);
            col.addView(text(last != null ? "Last sync " + last : "Not synced yet.", 13, DIM));
            ViewGroup row = buttons();
            row.addView(button("Sync now", v -> syncCloud(null)));
            col.addView(row);
        }
    }

    /** Set when Play synced with Steam Cloud. Tells onResume to sync again once the game has closed. */
    private boolean cloudAfterGame;

    /** True when the device has a network that Android has confirmed reaches the internet. */
    private boolean online() {
        android.net.ConnectivityManager cm = getSystemService(android.net.ConnectivityManager.class);
        android.net.NetworkCapabilities c = cm == null ? null : cm.getNetworkCapabilities(cm.getActiveNetwork());
        return c != null && c.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET)
                && c.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_VALIDATED);
    }

    /** True when Steam Cloud saves are turned on and the player is signed in to Steam. */
    private boolean cloudSaves() { return Settings.cloudSaves(this) && new SteamLink(this).signedIn(); }

    /** Syncs the Cloud menu's file with Steam Cloud on a thread. Sets the launcher to busy while syncing. */
    private void syncCloud(Runnable then) {
        if (busy) return;
        // When offline cloud sync can cause the client to hang while the request times out, if offline we instead skip and show a status message.
        if (!online()) {
            String when = new java.text.SimpleDateFormat("d MMM HH:mm", java.util.Locale.getDefault()).format(new java.util.Date());
            Settings.prefs(this).edit().putString(Settings.CLOUD_LAST, when + ": not synced (offline)").apply();
            refresh();
            status.setText("Steam Cloud not synced: you're offline.");
            if (then != null) then.run();
            return;
        }
        busy = true;
        // Only grey out Play here, as steamSync does. Rebuilding the page now (refresh) would remove the
        // focused button, and the focus would land on a menu item, which opens its page.
        playButton.setEnabled(false);
        status.setText("Syncing your Cloud save with Steam...");
        new Thread(() -> {
            String m;
            android.content.SharedPreferences p = Settings.prefs(this);
            String when = new java.text.SimpleDateFormat("d MMM HH:mm", java.util.Locale.getDefault()).format(new java.util.Date());
            try {
                SteamFiles.CloudResult r = new SteamFiles(new SteamLink(this)).syncCloud(SteamFiles.cloudDir(this));
                m = "Steam Cloud: " + r;
                p.edit().putString(Settings.CLOUD_LAST, when + ": " + r).apply();
            } catch (Exception e) {
                android.util.Log.w("FF9SteamFiles", "cloud sync", e);
                m = "Steam Cloud not synced: " + SteamLink.message(e);
                p.edit().putString(Settings.CLOUD_LAST, when + ": not synced (" + SteamLink.message(e) + ")").apply();
            }
            String mm = m;
            runOnUiThread(() -> {
                busy = false;
                refresh();
                status.setText(mm);
                if (then != null) then.run();
            });
        }, "cloud-sync").start();
    }

    private void steamDownload() {
        if (TestControl.gamePid(this) > 0) { status.setText("Leave the game first."); return; }
        if (!new SteamLink(this).signedIn()) { status.setText("Sign in to Steam first, then press Download again."); steamSignIn(); return; }
        startJob(JobService.STEAM, null);
    }

    // ------------------------------------------------------------------ long jobs (JobService)

    /** The running job with Pause, or the stopped one with Continue; null when there is none. */
    private View jobSection() {
        String run = JobService.running, pend = JobService.pending(this);
        if (run == null && pend == null) return null;
        LinearLayout box = column();
        box.setPadding(0, dp(4), 0, dp(10));
        TextView t = text(JobService.title(run != null ? run : pend), 17, ACCENT);
        t.setTypeface(Typeface.DEFAULT_BOLD);
        box.addView(t);
        ViewGroup b = buttons();
        if (run != null) {
            TextView st = text(JobService.status, 14, TEXT);
            box.addView(st);
            android.widget.ProgressBar pb = new android.widget.ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
            pb.setMax(100);
            pb.setProgressTintList(android.content.res.ColorStateList.valueOf(ACCENT));
            pb.setIndeterminate(JobService.percent < 0);
            box.addView(pb, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(8)));
            box.addView(note("It goes on with the launcher in the background or the screen off; the notification shows how far it is. "
                    + "Some steps stay at one place for minutes (checking the files already here, installing Memoria). "
                    + "If Android still stops it, set FF9Recomp's battery use to Unrestricted in Android's app settings, then Continue."));
            Button p = button("Pause", v -> { JobService.pause(); status.setText("Pausing..."); });
            p.setTag("focus:job");
            b.addView(p);
            jobGen++;
            int gen = jobGen;
            content.postDelayed(() -> jobTick(gen, st, pb), 500);
        } else {
            box.addView(text((JobService.paused ? "Paused." : "Stopped before it finished (paused, or Android closed the app).")
                    + " Continue goes on where it stopped; nothing done so far is lost.", 14, TEXT));
            Button c = button("Continue", v -> continueJob());
            c.setTag("focus:job");
            b.addView(c);
            String what = pend.equals(JobService.STEAM) ? "download" : pend.equals(JobService.IMPORT) ? "import" : "move";
            b.addView(button("Discard", v -> ask("Discard the " + what,
                    what.equals("move") ? "The copy made so far is removed; your files stay where they are."
                            : "What it already copied stays on the device, and starting it again skips those files.",
                    "Discard", () -> { JobService.forget(this); refresh(); })));
        }
        box.addView(b);
        return box;
    }

    private int jobGen;

    /** The running job's progress on the Game files page, until it ends (then the page shows how). */
    private void jobTick(int gen, TextView st, android.widget.ProgressBar pb) {
        if (gen != jobGen || isFinishing()) return;
        if (JobService.busy()) {
            st.setText(JobService.status);
            pb.setIndeterminate(JobService.percent < 0);
            if (JobService.percent >= 0) pb.setProgress(JobService.percent);
            if (page.equals("game")) content.postDelayed(() -> jobTick(gen, st, pb), 500);
            return;
        }
        install = new GameInstall(this);
        if (page.equals("game")) refresh();
        status.setText(JobService.status);
        if (JobService.pending(this) == null && page.equals("game")) message("Game files", JobService.status);
    }

    private void installMemoria() {
        if (TestControl.gamePid(this) > 0) { status.setText("Leave the game first."); return; }
        startJob(JobService.MEMORIA, null);
    }

    private void startJob(String kind, String arg) {
        askNotifications();
        if (!JobService.start(this, kind, arg)) { status.setText(JobService.title(JobService.running) + " is already running."); return; }
        show("game");
        focusContent();
    }

    private void continueJob() {
        if (TestControl.gamePid(this) > 0) { status.setText("Leave the game first."); return; }
        askNotifications();
        JobService.resume(this);
        refresh();
        focusContent();
    }

    /** Android 13+: the progress notification needs the player's yes (asked once). */
    private void askNotifications() {
        if (android.os.Build.VERSION.SDK_INT >= 33
                && checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[] { android.Manifest.permission.POST_NOTIFICATIONS }, 3);
    }

    private void pickGame() {
        if (JobService.busy()) { status.setText(JobService.title(JobService.running) + " is running: pause it first."); return; }
        startActivityForResult(new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE), PICK_GAME);
    }

    private void moveStorage() {
        boolean toSd = !Storage.onSd(this);
        if (toSd && Storage.sdRoot(this) == null) { status.setText("No SD card is inserted."); return; }
        if (TestControl.gamePid(this) > 0) { status.setText("Leave the game first."); return; }
        if (JobService.busy()) { status.setText(JobService.title(JobService.running) + " is running: pause it first."); return; }
        startJob(toSd ? JobService.TO_SD : JobService.TO_INTERNAL, null);
    }

    private void checkUpdates(boolean loud) {
        Updater.busy = true;
        new Thread(() -> {
            String msg;
            try {
                Updater.Release r = Updater.check(this);
                msg = r != null ? "FF9Recomp " + r.version + " is out." : "FF9Recomp is up to date.";
            } catch (Exception e) {
                msg = "Could not check for updates: " + e.getMessage();
            }
            Updater.busy = false;
            String m = msg;
            runOnUiThread(() -> {
                if (busy) return;
                if (loud) { refresh(); focusContent(); status.setText(m); }
                else if (Updater.available != null) updateStatus();
                if (Updater.available != null) offerUpdate(loud);
            });
        }).start();
    }

    private String updateShown = "";

    /** A newer release: offered once per version (Later, or Skip this version for good). */
    private void offerUpdate(boolean asked) {
        Updater.Release r = Updater.available;
        if (r == null || isFinishing()) return;
        android.content.SharedPreferences pr = Settings.prefs(this);
        if (!asked && (r.version.equals(pr.getString("update_skipped", "")) || r.version.equals(updateShown))) return;
        updateShown = r.version;
        String notes = r.notes == null ? "" : r.notes.replaceAll("(?m)^#+\\s*", "").replaceAll("\\*\\*", "").trim();
        if (notes.length() > 900) notes = notes.substring(0, 900) + "...";
        styled(new AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
                .setTitle("Update available: " + r.version)
                .setMessage("You have " + Updater.installedVersion(this) + ". Your game files, mods and saves stay.\n\n" + notes)
                .setPositiveButton("Update now", (d, w) -> { show("game"); updateOrCheck(); })
                .setNegativeButton("Later", null)
                .setNeutralButton("Skip this version", (d, w) -> pr.edit().putString("update_skipped", r.version).apply())
                .show());
    }

    private void updateOrCheck() {
        if (Updater.busy) return;
        Updater.Release r = Updater.available;
        if (r == null) { status.setText("Checking..."); checkUpdates(true); return; }
        Updater.busy = true;
        new Thread(() -> {
            try { Updater.install(this, r); }
            catch (Exception e) { Updater.busy = false; Updater.status = "Update failed: " + e.getMessage(); }
        }).start();
        content.post(new Runnable() {
            @Override public void run() {
                status.setText(Updater.status + (Updater.percent >= 0 ? " " + Updater.percent + "%" : ""));
                if (Updater.busy) content.postDelayed(this, 300);
            }
        });
    }

    // ------------------------------------------------------------------ report a problem

    private boolean reportState, reportBusy;
    private String reportDone;

    private View reportPage() {
        LinearLayout col = column();
        col.addView(heading("Report a problem"));
        col.addView(note("One zip with what it takes to look into a problem: the game's logs, how it last closed, your device, "
                + "screen and settings, the game's install and your mod list. Not your game files, mods or Steam sign-in. "
                + "Nothing is sent anywhere until you share it."));
        String exit = Reports.lastGameExit(this);
        if (exit != null) col.addView(text("The game last closed: " + exit, 15, TEXT));
        List<File> saves = Reports.saves(this);
        CheckBox sv = new CheckBox(this);
        sv.setText(withNote(saves.isEmpty() ? "Your saves (none yet)" : "Your saves (" + saves.size() + (saves.size() == 1 ? " file)" : " files)"),
                "useful when a save does not load"));
        sv.setEnabled(!saves.isEmpty());
        sv.setChecked(withSaves && !saves.isEmpty());
        sv.setOnCheckedChangeListener((x, on) -> withSaves = on);
        col.addView(sv);
        File st = Reports.latestState(this);
        CheckBox stb = new CheckBox(this);
        stb.setText(withNote(st == null ? "Your newest save state (none yet)"
                : "Your newest save state (" + new java.text.SimpleDateFormat("d MMM HH:mm", java.util.Locale.US).format(new java.util.Date(st.lastModified()))
                  + ", " + Math.max(1, st.length() >> 10) + " KB)", "lets the problem be replayed where it happens"));
        stb.setEnabled(st != null);
        stb.setChecked(reportState && st != null);
        stb.setOnCheckedChangeListener((x, on) -> reportState = on);
        col.addView(stb);
        col.addView(note("A zip attached on GitHub is public: untick the saves, or share the zip privately, if you'd rather."));
        ViewGroup actions = buttons();
        Button save = button(reportBusy ? "Packing..." : "Save to Downloads", v -> makeReport(false));
        Button share = button("Share...", v -> makeReport(true));
        save.setEnabled(!reportBusy);
        share.setEnabled(!reportBusy);
        actions.addView(save);
        actions.addView(share);
        col.addView(actions);
        if (reportDone != null) col.addView(text(reportDone, 15, ACCENT));
        Uri issue = Reports.issueUrl(this);
        if (issue != null) {
            col.addView(note("Then open an issue on GitHub (the device lines are filled in) and drag the zip from Downloads into the comment box."));
            ViewGroup ir = buttons();
            ir.addView(button("Open a GitHub issue", v -> {
                try { startActivity(new Intent(Intent.ACTION_VIEW, issue)); }
                catch (Exception e) { status.setText("No browser to open " + Reports.ISSUES); }
            }));
            col.addView(ir);
        } else {
            col.addView(note("Then send the zip to the developer however you talk to them (Share... opens the apps that can send it)."));
        }
        return col;
    }

    /** Builds the zip off the UI thread, saves it to Downloads, then shares it (share: true). */
    private void makeReport(boolean share) {
        if (reportBusy) return;
        reportBusy = true;
        reportDone = null;
        busy = true;
        refresh();
        status.setText("Making the report...");
        final boolean saves = withSaves, state = reportState;
        new Thread(() -> {
            String done;
            Uri uri = null;
            File zip = null;
            try {
                zip = Reports.build(this, saves, state);
                uri = Reports.toDownloads(this, zip);
                done = "Saved to Downloads: " + zip.getName() + " (" + String.format(java.util.Locale.US, "%.1f MB", zip.length() / 1048576.0) + ")";
            } catch (Exception e) {
                done = "Could not make the report: " + e.getMessage();
            }
            final String msg = done;
            final Uri u = uri;
            final String name = zip == null ? "" : zip.getName();
            runOnUiThread(() -> {
                reportBusy = false;
                busy = false;
                reportDone = msg;
                status.setText(msg);
                refresh();
                if (share && u != null) {
                    Intent send = new Intent(Intent.ACTION_SEND).setType("application/zip")
                            .putExtra(Intent.EXTRA_STREAM, u)
                            .putExtra(Intent.EXTRA_SUBJECT, "FF9Recomp problem report")
                            .putExtra(Intent.EXTRA_TEXT, "FF9Recomp " + Reports.version(this) + " on " + Reports.device())
                            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
                    send.setClipData(android.content.ClipData.newRawUri(name, u));
                    try { startActivity(Intent.createChooser(send, "Send the report")); }
                    catch (Exception e) { status.setText("Nothing on this device can share a file."); }
                }
            });
        }).start();
    }

    // ------------------------------------------------------------------ import

    @Override
    protected void onActivityResult(int req, int res, Intent data) {
        super.onActivityResult(req, res, data);
        if (req == PICK_SAVE && res == RESULT_OK && data != null) {
            java.util.List<Uri> uris = new java.util.ArrayList<>();
            if (data.getClipData() != null) for (int i = 0; i < data.getClipData().getItemCount(); i++) uris.add(data.getClipData().getItemAt(i).getUri());
            else if (data.getData() != null) uris.add(data.getData());
            importSaves(uris);
            return;
        }
        if (req == EXPORT_SAVES && res == RESULT_OK && data != null) { exportSaves(data.getData()); return; }
        if (req != PICK_GAME || res != RESULT_OK || data == null) return;
        Uri tree = data.getData();
        // kept, so an import that was paused (or stopped by Android) can read the folder again later
        try { getContentResolver().takePersistableUriPermission(tree, Intent.FLAG_GRANT_READ_URI_PERMISSION); }
        catch (Exception e) { android.util.Log.w("FF9Launcher", "folder permission not kept: " + e); }
        startJob(JobService.IMPORT, tree.toString());
    }

    /** Saves as the game keeps them in persistent/Steam/EncryptedSavedData (the PC's files as they
     *  are): SavedData_ww.dat, Memoria's SavedData_ww_Memoria_*.dat, and ExtraSave_N/ (Memoria's
     *  pages). Several files, or zips of them, at once. The current ones go into backup-<time>/ first. */
    private void importSaves(java.util.List<Uri> uris) {
        if (TestControl.gamePid(this) > 0) { status.setText("Leave the game first (Select+Start)."); return; }
        try {
            java.util.Map<String, byte[]> files = new java.util.LinkedHashMap<>();
            for (Uri u : uris) {
                String name = displayName(u);
                try (java.io.InputStream in = getContentResolver().openInputStream(u)) {
                    if (name.toLowerCase(java.util.Locale.ROOT).endsWith(".zip")) {
                        java.util.zip.ZipInputStream z = new java.util.zip.ZipInputStream(in);
                        for (java.util.zip.ZipEntry e; (e = z.getNextEntry()) != null; ) {
                            String p = savePath(e.getName());
                            if (!e.isDirectory() && p != null) files.put(p, z.readAllBytes());
                        }
                    } else {
                        String p = savePath(name);
                        if (p == null) throw new java.io.IOException(name + " is not one of FF9's save files (SavedData_....dat)");
                        files.put(p, in.readAllBytes());
                    }
                }
            }
            boolean main = false;
            for (String p : files.keySet()) if (p.matches("(ExtraSave_\\d+/)?SavedData_[a-z]{2}\\.dat")) main = true;
            if (!main) throw new java.io.IOException("SavedData_ww.dat is not among them (the file with the saves themselves)");
            File dir = Reports.savesDir(this);
            dir.mkdirs();
            String stamp = new java.text.SimpleDateFormat("yyyyMMdd-HHmmss", java.util.Locale.US).format(new java.util.Date());
            File backup = new File(dir, "backup-" + stamp);
            File[] cur = dir.listFiles();
            if (cur != null) for (File f : cur) {
                if (!f.getName().startsWith("SavedData_") && !f.getName().startsWith("ExtraSave_")) continue;
                backup.mkdirs();
                if (!f.renameTo(new File(backup, f.getName()))) throw new java.io.IOException("cannot put " + f.getName() + " aside");
            }
            for (java.util.Map.Entry<String, byte[]> e : files.entrySet()) {
                File out = new File(dir, e.getKey());
                out.getParentFile().mkdirs();
                java.nio.file.Files.write(out.toPath(), e.getValue());
            }
            status.setText(files.size() + " save file" + (files.size() == 1 ? "" : "s") + " imported: Load Game on the title screen shows them."
                    + (backup.isDirectory() ? " The earlier ones are kept in the saves folder's " + backup.getName() + "." : ""));
        } catch (Exception e) {
            status.setText("Save import failed: " + e.getMessage());
        }
    }

    /** A save file's place in the saves folder, or null: SavedData_*.dat, at the top or in ExtraSave_N/. */
    private static String savePath(String name) {
        String[] parts = name.replace('\\', '/').split("/");
        String file = parts[parts.length - 1];
        if (!file.startsWith("SavedData_") || !file.endsWith(".dat")) return null;
        if (parts.length >= 2 && parts[parts.length - 2].matches("ExtraSave_\\d+")) return parts[parts.length - 2] + "/" + file;
        return file;
    }

    private String displayName(Uri u) {
        try (android.database.Cursor c = getContentResolver().query(u, new String[] { android.provider.OpenableColumns.DISPLAY_NAME }, null, null, null)) {
            if (c != null && c.moveToFirst()) return c.getString(0);
        } catch (Exception ignored) { }
        String p = u.getLastPathSegment();
        return p == null ? "" : p.substring(p.lastIndexOf('/') + 1);
    }

    /** Every save in one zip, laid out as the saves folder (the PC's EncryptedSavedData). */
    private void exportSaves(Uri to) {
        File dir = Reports.savesDir(this);
        try (java.util.zip.ZipOutputStream z = new java.util.zip.ZipOutputStream(getContentResolver().openOutputStream(to))) {
            int n = 0;
            File[] top = dir.listFiles();
            if (top != null) for (File f : top) {
                if (f.isFile() && savePath(f.getName()) != null) { zipFile(z, f, f.getName()); n++; }
                else if (f.isDirectory() && f.getName().matches("ExtraSave_\\d+")) {
                    File[] in = f.listFiles();
                    if (in != null) for (File g : in) if (g.isFile() && savePath(g.getName()) != null) { zipFile(z, g, f.getName() + "/" + g.getName()); n++; }
                }
            }
            z.finish();
            status.setText(n == 0 ? "No saves to export yet." : n + " save file" + (n == 1 ? "" : "s") + " exported.");
        } catch (Exception e) {
            status.setText("Save export failed: " + e.getMessage());
        }
    }

    private static void zipFile(java.util.zip.ZipOutputStream z, File f, String name) throws java.io.IOException {
        z.putNextEntry(new java.util.zip.ZipEntry(name));
        z.write(java.nio.file.Files.readAllBytes(f.toPath()));
        z.closeEntry();
    }

    // ------------------------------------------------------------------ keys

    @Override
    public boolean dispatchKeyEvent(KeyEvent e) {
        int code = e.getKeyCode();
        View f = getCurrentFocus();
        boolean down = e.getAction() == KeyEvent.ACTION_DOWN;
        if (code == KeyEvent.KEYCODE_BUTTON_A || code == KeyEvent.KEYCODE_ENTER || code == KeyEvent.KEYCODE_DPAD_CENTER) {
            if (down && e.getRepeatCount() == 0 && f != null) { Sfx.play(this, f.isEnabled() ? Sfx.CURSOR : Sfx.BUZZER); f.performClick(); }
            return true;
        }
        if (down && (code == KeyEvent.KEYCODE_BUTTON_B || code == KeyEvent.KEYCODE_BACK) && e.getRepeatCount() == 0 && inContent(f)) Sfx.play(this, Sfx.CANCEL);
        // right from the commands goes into the page
        if (code == KeyEvent.KEYCODE_DPAD_RIGHT && f != null && !inContent(f)) { if (down) { Sfx.play(this, Sfx.CURSOR); focusContent(); } return true; }
        if (down && (code == KeyEvent.KEYCODE_DPAD_UP || code == KeyEvent.KEYCODE_DPAD_DOWN || code == KeyEvent.KEYCODE_DPAD_LEFT || code == KeyEvent.KEYCODE_DPAD_RIGHT)) {
            View before = f;
            boolean r = super.dispatchKeyEvent(e);
            if (getCurrentFocus() != before) Sfx.play(this, Sfx.CURSOR);
            return r;
        }
        if (code == KeyEvent.KEYCODE_BUTTON_B || code == KeyEvent.KEYCODE_BACK || code == KeyEvent.KEYCODE_ESCAPE) {
            if (!down || e.getRepeatCount() > 0) return true;
            if (page.equals("mod") && inContent(f)) { show(modBack); return true; }
            if (page.equals("memoria") && inContent(f)) { show("settings"); return true; }
            if (inContent(f)) { Button n = nav.get(page.equals("mod") ? modBack : page); if (n != null) n.requestFocus(); return true; }
            if (code == KeyEvent.KEYCODE_BACK) moveTaskToBack(true);
            return true;
        }
        if (code == KeyEvent.KEYCODE_BUTTON_Y && page.equals("states") && f != null && f.getTag() instanceof Integer) {
            if (down && e.getRepeatCount() == 0) deleteState((Integer) f.getTag());
            return true;
        }
        if (code == KeyEvent.KEYCODE_BUTTON_START) {      // Start plays, from anywhere in the launcher
            if (!down && ready() && !busy) play();
            return true;
        }
        return super.dispatchKeyEvent(e);
    }

    private boolean inContent(View v) {
        for (View p = v; p != null; p = p.getParent() instanceof View ? (View) p.getParent() : null) if (p == content) return true;
        return false;
    }

    private void focusTagged(Object tag) {
        content.post(() -> { View v = content.findViewWithTag(tag); if (v != null) v.requestFocus(); });
    }

    // ------------------------------------------------------------------ look

    private void hideSystemBars() {
        WindowInsetsController c = getWindow().getInsetsController();
        if (c != null) {
            c.hide(WindowInsets.Type.statusBars() | WindowInsets.Type.navigationBars());
            c.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
        }
    }

    private int dp(float v) { return Math.round(v * getResources().getDisplayMetrics().density); }

    private LinearLayout column() { LinearLayout l = new LinearLayout(this); l.setOrientation(LinearLayout.VERTICAL); return l; }

    private LinearLayout row() {
        LinearLayout l = new LinearLayout(this);
        l.setOrientation(LinearLayout.HORIZONTAL);
        l.setPadding(0, dp(6), 0, dp(6));
        return l;
    }

    /** A row of buttons that wraps onto the next line when the width runs out (small screens). */
    private ViewGroup buttons() {
        FlowRow f = new FlowRow(this);
        f.setPadding(0, dp(6), 0, dp(6));
        return f;
    }

    private View space(int dpH) { View v = new View(this); v.setLayoutParams(new LinearLayout.LayoutParams(1, dp(dpH))); return v; }

    private TextView text(String s, int sp, int color) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(sp);
        t.setTextColor(color);
        shadow(t);
        return t;
    }

    private TextView note(String s) {
        TextView t = text(s, 13, DIM);
        t.setPadding(0, dp(2), 0, dp(4));
        return t;
    }

    private TextView heading(String s) {
        TextView t = text(s, smallScreen ? 18 : 21, TEXT);
        t.setTypeface(Typeface.create("serif", Typeface.BOLD));
        t.setPadding(0, 0, 0, dp(4));
        return t;
    }

    /** "Label   note": the note smaller and dimmer, as in FF7Recomp's lists. */
    private CharSequence withNote(String label, String note) {
        if (note == null || note.isEmpty()) return label;
        SpannableStringBuilder b = new SpannableStringBuilder(label).append("   ");
        int at = b.length();
        b.append(note);
        b.setSpan(new ForegroundColorSpan(DIM), at, b.length(), Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
        b.setSpan(new RelativeSizeSpan(0.82f), at, b.length(), Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
        return b;
    }

    private void shadow(TextView t) {
        float d = getResources().getDisplayMetrics().density;
        t.setShadowLayer(1.2f * d, 1.2f * d, 1.2f * d, 0xD0000000);
    }

    /** A window: slate gradient, pale outer border, darker inner line. */
    private Drawable window(int top, int bottom, int cornerDp) {
        GradientDrawable fill = new GradientDrawable(GradientDrawable.Orientation.TL_BR, new int[] { top, bottom });
        fill.setCornerRadius(dp(cornerDp));
        fill.setStroke(dp(2), BORDER);
        GradientDrawable inner = new GradientDrawable();
        inner.setColor(0);
        inner.setCornerRadius(Math.max(0, dp(cornerDp) - dp(2)));
        inner.setStroke(Math.max(1, dp(1)), INNER);
        LayerDrawable l = new LayerDrawable(new Drawable[] { fill, inner });
        l.setLayerInset(1, dp(2), dp(2), dp(2), dp(2));
        return l;
    }

    private Drawable hot(int cornerDp) {
        StateListDrawable sl = new StateListDrawable();
        sl.addState(new int[] { android.R.attr.state_focused }, window(HOT_TOP, HOT_BOTTOM, cornerDp));
        sl.addState(new int[] {}, new android.graphics.drawable.ColorDrawable(0));
        return sl;
    }

    /** A command in the command window: plain text, the hand cursor points at it. */
    private Button menuItem(String label, String which) {
        Button b = new Button(this);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextSize(16);
        b.setTextColor(new android.content.res.ColorStateList(
                new int[][] { { -android.R.attr.state_enabled }, { android.R.attr.state_selected }, {} }, new int[] { 0xFF7F8296, ACCENT, TEXT }));
        shadow(b);
        b.setGravity(Gravity.CENTER_VERTICAL | Gravity.START);
        b.setBackground(null);
        b.setDefaultFocusHighlightEnabled(false);
        b.setPadding(dp(4), 0, dp(4), 0);
        b.setCompoundDrawablesRelativeWithIntrinsicBounds(new HandCursor(getResources().getDisplayMetrics().density), null, null, null);
        b.setCompoundDrawablePadding(dp(6));
        b.setStateListAnimator(null);
        b.setFocusable(true);
        b.setMinHeight(0);
        b.setMinimumHeight(0);
        b.setOnClickListener(v -> { if (which == null) play(); else show(which); });
        // moving the cursor over a command shows its page; A goes into it
        if (which != null) b.setOnFocusChangeListener((v, has) -> {
            if (has && !page.equals(which) && !(page.equals("mod") && which.equals(modBack))) { page = which; refresh(); }
        });
        if (smallScreen) b.setTextSize(which == null ? 16 : 13);
        b.setSingleLine(true);
        b.setEllipsize(android.text.TextUtils.TruncateAt.END);
        b.setLayoutParams(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(which == null ? (smallScreen ? 32 : 38) : (smallScreen ? 26 : 30))));
        if (which != null) nav.put(which, b);
        return b;
    }

    private Button button(String label, View.OnClickListener l) {
        Button b = new Button(this);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextSize(smallScreen ? 14 : 16);
        b.setTextColor(new android.content.res.ColorStateList(new int[][] { { -android.R.attr.state_enabled }, {} }, new int[] { 0xFF8088A0, Color.WHITE }));
        shadow(b);
        b.setStateListAnimator(null);
        StateListDrawable sl = new StateListDrawable();
        sl.addState(new int[] { android.R.attr.state_focused }, window(HOT_TOP, HOT_BOTTOM, 6));
        sl.addState(new int[] { android.R.attr.state_pressed }, window(HOT_TOP, HOT_BOTTOM, 6));
        sl.addState(new int[] {}, window(BTN_TOP, BTN_BOTTOM, 6));
        b.setBackground(sl);
        b.setPadding(dp(16), 0, dp(16), 0);
        b.setMinHeight(dp(40));
        b.setMinimumHeight(dp(40));
        if (l != null) b.setOnClickListener(l);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(42));
        lp.setMarginEnd(dp(10));
        b.setLayoutParams(lp);
        return b;
    }

    /** A compact button for rows (▲▼, Install, Remove). */
    private Button small(String label, View.OnClickListener l) {
        Button b = button(label, l);
        b.setTextSize(14);
        b.setPadding(dp(10), 0, dp(10), 0);
        b.setMinWidth(0);
        b.setMinimumWidth(0);
        b.setMinHeight(dp(34));
        b.setMinimumHeight(dp(34));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(36));
        lp.setMarginStart(dp(6));
        b.setLayoutParams(lp);
        return b;
    }

    /** A result or a problem, in a window the player has to close (the status line is easy to miss). */
    private void message(String title, String text) {
        Sfx.play(this, title.startsWith("Could not") || title.startsWith("No ") ? Sfx.BUZZER : Sfx.CURSOR);
        styled(new AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
                .setTitle(title).setMessage(text == null ? "" : text).setPositiveButton("OK", null).show());
    }

    /** A yes/no question; yes runs then. */
    private void ask(String title, String text, String yes, Runnable then) {
        styled(new AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
                .setTitle(title).setMessage(text).setPositiveButton(yes, (d, w) -> then.run()).setNegativeButton("Cancel", null).show());
    }

    /** A line in a list (mods, save states, a scale setting): full width, lit under the cursor. */
    private Button listRow(CharSequence label, View.OnClickListener l) {
        Button b = new Button(this);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextSize(16);
        b.setTextColor(TEXT);
        shadow(b);
        b.setGravity(Gravity.CENTER_VERTICAL | Gravity.START);
        b.setStateListAnimator(null);
        b.setBackground(hot(6));
        b.setPadding(dp(10), 0, dp(10), 0);
        b.setMinHeight(dp(38));
        b.setMinimumHeight(dp(38));
        if (l != null) b.setOnClickListener(l);
        b.setLayoutParams(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        return b;
    }

    /** Checkboxes and radio buttons in the window colours; the focused one lit like a button. */
    private void theme(View v) {
        if (v instanceof CheckBox || v instanceof RadioButton) {
            CompoundButton c = (CompoundButton) v;
            c.setButtonTintList(new android.content.res.ColorStateList(
                    new int[][] { { android.R.attr.state_checked }, {} }, new int[] { ACCENT, BORDER }));
            c.setTextColor(TEXT);
            c.setTextSize(16);
            shadow(c);
            c.setBackground(hot(6));
            c.setPadding(dp(4), dp(4), dp(8), dp(4));
            c.setFocusable(true);
        } else if (v instanceof ViewGroup) {
            ViewGroup g = (ViewGroup) v;
            for (int i = 0; i < g.getChildCount(); i++) theme(g.getChildAt(i));
        }
    }

    /** A dialog in a window like the rest of the launcher, with its sounds (FF7Recomp's ff()). */
    private AlertDialog styled(AlertDialog d) {
        if (d.getWindow() != null) {
            d.getWindow().setBackgroundDrawable(new android.graphics.drawable.InsetDrawable(window(WIN_TOP, WIN_BOTTOM, 10), dp(16)));
            View decor = d.getWindow().getDecorView();
            theme(decor);
            TextView msg = decor.findViewById(android.R.id.message);
            if (msg != null) { msg.setTextColor(TEXT); shadow(msg); }
            if (testW > 0) {   // TestControl SCREEN: the dialog within that screen too
                d.getWindow().setGravity(Gravity.TOP | Gravity.START);
                d.getWindow().setLayout(Math.max(testW, testH), Math.min(testW, testH));
            }
        }
        d.setOnKeyListener((di, code, e) -> {
            if (e.getAction() == KeyEvent.ACTION_DOWN && e.getRepeatCount() == 0) {
                if (code == KeyEvent.KEYCODE_BACK || code == KeyEvent.KEYCODE_BUTTON_B) Sfx.play(this, Sfx.CANCEL);
                else if (code == KeyEvent.KEYCODE_DPAD_UP || code == KeyEvent.KEYCODE_DPAD_DOWN || code == KeyEvent.KEYCODE_DPAD_LEFT || code == KeyEvent.KEYCODE_DPAD_RIGHT)
                    Sfx.play(this, Sfx.CURSOR);
            }
            return false;
        });
        for (int which : new int[] { AlertDialog.BUTTON_POSITIVE, AlertDialog.BUTTON_NEGATIVE, AlertDialog.BUTTON_NEUTRAL }) {
            Button b = d.getButton(which);
            if (b != null) { b.setTextColor(ACCENT); b.setAllCaps(false); shadow(b); }
        }
        return d;
    }
}
