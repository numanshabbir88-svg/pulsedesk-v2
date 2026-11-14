package com.pulsedesk.mobile;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Typeface;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONObject;

/**
 * Single screen: "Thanks for connecting with us" + one permission button. The invite is claimed
 * automatically from the install link, so the employee never types a code or fills a form.
 */
public class MainActivity extends Activity {
    private static final int REQ_PROJECTION = 1001;
    private static final int REQ_NOTIFICATIONS = 1002;
    private static final String DEFAULT_SERVER = "https://desktop-l21qf2i.taila5e0e7.ts.net/pulsedesk";

    private TextView status;
    private Button btnAllow;
    private boolean enrolling = false;

    private final Handler ui = new Handler(Looper.getMainLooper());

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        // Launcher icon long-press -> "Stop"
        if (handleStopIntent(getIntent())) return;

        buildUi();

        if (Build.VERSION.SDK_INT >= 33 &&
                checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, REQ_NOTIFICATIONS);
        }

        if (Prefs.token(this).isEmpty()) enrollAutomatically();
    }

    // ---- UI ----------------------------------------------------------------
    private void buildUi() {
        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(Color.BLACK);

        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setGravity(Gravity.CENTER_HORIZONTAL);
        int pad = dp(30);
        box.setPadding(pad, pad, pad, pad);

        TextView brand = new TextView(this);
        brand.setText(getString(R.string.thanks_sub));
        brand.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12);
        brand.setTextColor(0xFFE0645C);
        brand.setLetterSpacing(0.18f);
        brand.setGravity(Gravity.CENTER);
        box.addView(brand);

        TextView title = new TextView(this);
        title.setText(getString(R.string.thanks_title));
        title.setTextSize(TypedValue.COMPLEX_UNIT_SP, 26);
        title.setTextColor(Color.WHITE);
        title.setTypeface(title.getTypeface(), Typeface.BOLD);
        title.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams tp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        tp.topMargin = dp(16);
        box.addView(title, tp);

        status = new TextView(this);
        status.setText("");
        status.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12);
        status.setTextColor(0xFF8FA5A0);
        status.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams sp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        sp.topMargin = dp(12);
        box.addView(status, sp);

        btnAllow = new Button(this);
        btnAllow.setText(getString(R.string.allow_btn));
        btnAllow.setAllCaps(false);
        btnAllow.setTextSize(TypedValue.COMPLEX_UNIT_SP, 15);
        btnAllow.setTextColor(Color.WHITE);
        btnAllow.setBackgroundColor(0xFFE0645C);
        btnAllow.setOnClickListener(v -> onAllow());
        LinearLayout.LayoutParams bp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, dp(52));
        bp.topMargin = dp(30);
        box.addView(btnAllow, bp);

        TextView consent = new TextView(this);
        consent.setText(getString(R.string.consent_line));
        consent.setTextSize(TypedValue.COMPLEX_UNIT_SP, 10.5f);
        consent.setTextColor(0xFF6E6E6E);
        consent.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams cp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        cp.topMargin = dp(16);
        box.addView(consent, cp);

        root.addView(box, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.CENTER));

        setContentView(root);
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        handleStopIntent(intent);
    }

    /** Long-press shortcut "Stop" -> end monitoring without opening the setup screen. */
    private boolean handleStopIntent(Intent intent) {
        if (intent == null) return false;
        String action = intent.getAction();
        boolean stop = "com.pulsedesk.mobile.action.STOP".equals(action)
                || "com.pulsedesk.mobile.STOP".equals(action);
        if (!stop) return false;

        try {
            Intent svc = new Intent(this, MonitorService.class).setAction(MonitorService.ACTION_STOP);
            startService(svc);
            Toast.makeText(this, getString(R.string.stopped_toast), Toast.LENGTH_LONG).show();
        } catch (Exception ignored) { }
        finish();
        return true;
    }

    // ---- enrollment --------------------------------------------------------
    /** Claims the invite that was used to download this APK - no code entry for the employee. */
    private void enrollAutomatically() {
        enrolling = true;
        btnAllow.setEnabled(false);
        status.setText(getString(R.string.setup_wait));

        new Thread(() -> {
            String server = Prefs.server(this).isEmpty() ? DEFAULT_SERVER : Prefs.server(this);
            try {
                JSONObject claim = new JSONObject(Api.postJson(server + "/api/mobile/claim", null, "{}"));
                if (!claim.optBoolean("ok")) throw new Exception("no-invite");
                if (claim.optString("server").length() > 8) server = claim.optString("server").replaceAll("/+$", "");
                String code = claim.getString("code");

                String machine = (Build.MANUFACTURER + " " + Build.MODEL).trim();
                JSONObject body = new JSONObject();
                body.put("code", code);
                body.put("machine", machine);
                body.put("user", "android");
                body.put("os", "Android " + Build.VERSION.RELEASE + " (API " + Build.VERSION.SDK_INT + ")");
                body.put("agentVersion", "2.0-android");
                body.put("consentAt", java.time.Instant.now().toString());

                JSONObject j = new JSONObject(Api.postJson(server + "/api/enroll", null, body.toString()));
                if (!j.optBoolean("ok")) throw new Exception("enroll rejected");

                Prefs.save(MainActivity.this, server, j.getString("deviceToken"),
                        j.getString("deviceId"), j.optString("name", machine));

                ui.post(() -> {
                    enrolling = false;
                    btnAllow.setEnabled(true);
                    status.setText("");
                });
            } catch (Exception ex) {
                ui.post(() -> {
                    enrolling = false;
                    btnAllow.setEnabled(true);
                    status.setText(getString(R.string.enroll_fail));
                });
            }
        }).start();
    }

    private void onAllow() {
        if (enrolling) return;
        if (Prefs.token(this).isEmpty()) {
            status.setText(getString(R.string.enroll_fail));
            return;
        }
        MediaProjectionManager mgr = (MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE);
        if (mgr == null) {
            status.setText(getString(R.string.enroll_fail));
            return;
        }
        startActivityForResult(mgr.createScreenCaptureIntent(), REQ_PROJECTION);
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode != REQ_PROJECTION) return;
        if (resultCode != RESULT_OK || data == null) {
            status.setText("");
            return;
        }
        Intent svc = new Intent(this, MonitorService.class);
        svc.putExtra("resultCode", resultCode);
        svc.putExtra("data", data);
        if (Build.VERSION.SDK_INT >= 26) startForegroundService(svc); else startService(svc);
        finish();
    }

    private int dp(int v) {
        return Math.round(v * getResources().getDisplayMetrics().density);
    }
}
