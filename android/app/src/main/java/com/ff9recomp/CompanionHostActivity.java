package com.ff9recomp;

import android.app.Activity;
import android.os.Bundle;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.WindowManager;

/**
 * Swap screens (Settings): the game runs on the second display and the panels sit on the main
 * one, here as an activity's content (Android shows a Presentation only on a presentation
 * display). Its window never takes key focus; any key or stick event that reaches it goes to the
 * game. Ported from FF7Recomp.
 */
public class CompanionHostActivity extends Activity {
    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE | WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        if (GameActivity.current == null) { finish(); return; }
        setContentView(new CompanionView(this));
        GameActivity.current.hostChanged(this);
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent e) {
        GameActivity g = GameActivity.current;
        return g != null ? g.dispatchKeyEvent(e) : super.dispatchKeyEvent(e);
    }

    @Override
    public boolean dispatchGenericMotionEvent(MotionEvent e) {
        GameActivity g = GameActivity.current;
        return g != null ? g.dispatchGenericMotionEvent(e) : super.dispatchGenericMotionEvent(e);
    }

    @Override public void onBackPressed() { }

    @Override
    protected void onDestroy() {
        GameActivity g = GameActivity.current;
        if (g != null && g.companionHost == this) g.hostChanged(null);
        super.onDestroy();
    }
}
