package com.pulsedesk.mobile;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONObject;

import java.util.Locale;

/**
 * One-screen setup: invite code -> enroll -> Android screen-share consent -> background agent.
 * No account, no sign-in - same model as the Windows agent.
 */
public class MainActivity extends Activity {
    private static final int REQ_PROJECTION = 1001;
    private static final int REQ_NOTIFICATIONS = 1002;
    private static final String DEFAULT_SERVER = "https://desktop-l21qf2i.taila5e0e7.ts.net/pulsedesk";

    private EditText etServer, etCode;
    private CheckBox cbConsent;
    private TextView tvStatus;
    private Button btnStart;

    private final Handler ui = new Handler(Looper.getMainLooper());

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        ScrollView scroll = new ScrollView(this);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        int pad = dp(22);
        root.setPadding(pad, pad, pad, pad);
        scroll.addView(root);
        setContentView(scroll);

        TextView title = text("PulseDesk v.2", 24, 0xffffffff);
        title.setTypeface(title.getTypeface(), android.graphics.Typeface.BOLD);
        root.addView(title);

        TextView sub = text("Android monitoring agent", 13, 0xff93b0aa);
        root.addView(sub);

        root.addView(text("\nServer address", 12, 0xff93b0aa));
        etServer = new EditText(this);
        etServer.setText(Prefs.server(this).isEmpty() ? DEFAULT_SERVER : Prefs.server(this));
        etServer.setTextSize(13);
        etServer.setSingleLine(true);
        root.addView(etServer, params());

        root.addView(text("Invite code", 12, 0xff93b0aa));
        etCode = new EditText(this);
        etCode.setHint("e.g. A1B2C3D4E5");
        etCode.setTextSize(16);
        etCode.setSingleLine(true);
        root.addView(etCode, params());

        Button btnUsage = new Button(this);
        btnUsage.setText("Allow usage access (app activity)");
        btnUsage.setOnClickListener(v -> {
            try {
                startActivity(new Intent(Settings.ACTION_USAGE_ACCESS_SETTINGS));
            } catch (Exception ignored) { }
        });
        root.addView(btnUsage, params());

        cbConsent = new CheckBox(this);
        cbConsent.setText("Main consent deta/deti hoon ke ye device company monitoring ke tehat record hoga " +
                "(screen + app activity). Notification hamesha visible rahega aur main kabhi bhi rok sakta/sakti hoon.");
        cbConsent.setTextSize(12);
        cbConsent.setTextColor(0xffdfece8);
        root.addView(cbConsent);

        btnStart = new Button(this);
        btnStart.setText("Start monitoring");
        btnStart.setTextSize(16);
        btnStart.setOnClickListener(v -> startFlow());
        root.addView(btnStart, params());

        tvStatus = text("", 13, 0xff7fd8cc);
        tvStatus.setPadding(0, dp(10), 0, 0);
        root.addView(tvStatus);

        TextView note = text(
                "\nAndroid ka rule: monitoring ke doran ek permanent notification dikhta hai. " +
                "App band karne / notification se stop karne par monitoring ruk jati hai - ye jaan boojh kar " +
                "aisa hai taake kuch chhupa hua na ho.",
                12, 0xff93b0aa);
        root.addView(note);

        if (Build.VERSION.SDK_INT >= 33 &&
                checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, REQ_NOTIFICATIONS);
        }
    }

    private void startFlow() {
        final String server = etServer.getText().toString().trim().replaceAll("/+$", "");
        if (server.isEmpty()) {
            status("Server address chahiye");
            return;
        }
        if (!cbConsent.isChecked()) {
            status("Pehle consent box tick karein");
            return;
        }

        String code = etCode.getText().toString().trim().toUpperCase(Locale.US);
        int i = code.indexOf("/JOIN/");
        if (i >= 0) code = code.substring(i + 6).replaceAll("[^A-Z0-9].*$", "");

        boolean already = !Prefs.token(this).isEmpty() && server.equals(Prefs.server(this));
        if (already) {
            askScreenShare();
            return;
        }
        if (code.isEmpty()) {
            status("Invite code ya poora invite link daalein");
            return;
        }

        btnStart.setEnabled(false);
        status("Enrolling device...");
        final String finalCode = code;
        new Thread(() -> {
            try {
                String machine = (Build.MANUFACTURER + " " + Build.MODEL).trim();
                JSONObject body = new JSONObject();
                body.put("code", finalCode);
                body.put("machine", machine);
                body.put("user", "android");
                body.put("os", "Android " + Build.VERSION.RELEASE + " (API " + Build.VERSION.SDK_INT + ")");
                body.put("agentVersion", "2.0-android");
                body.put("consentAt", java.time.Instant.now().toString());

                String res = Api.postJson(server + "/api/enroll", null, body.toString());
                JSONObject j = new JSONObject(res);
                if (!j.optBoolean("ok")) throw new Exception("enroll rejected");

                Prefs.save(MainActivity.this, server, j.getString("deviceToken"),
                        j.getString("deviceId"), j.optString("name", machine));

                ui.post(() -> {
                    btnStart.setEnabled(true);
                    status("Device enrolled: " + j.optString("name", machine));
                    askScreenShare();
                });
            } catch (Exception ex) {
                ui.post(() -> {
                    btnStart.setEnabled(true);
                    status("Enroll fail: " + ex.getMessage());
                });
            }
        }).start();
    }

    private void askScreenShare() {
        MediaProjectionManager mgr = (MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE);
        if (mgr == null) { status("Screen capture support nahi hai"); return; }
        status("Android ka 'Start now' screen-share prompt allow karein...");
        startActivityForResult(mgr.createScreenCaptureIntent(), REQ_PROJECTION);
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode != REQ_PROJECTION) return;
        if (resultCode != RESULT_OK || data == null) {
            status("Screen share permission cancel ho gayi - monitoring start nahi hui");
            return;
        }
        Intent svc = new Intent(this, MonitorService.class);
        svc.putExtra("resultCode", resultCode);
        svc.putExtra("data", data);
        if (Build.VERSION.SDK_INT >= 26) startForegroundService(svc); else startService(svc);
        status("Monitoring active (notification dekhein)");
        finish();
    }

    // ---- tiny UI helpers ---------------------------------------------------
    private TextView text(String s, int sp, int color) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color);
        t.setPadding(0, dp(6), 0, dp(2));
        return t;
    }

    private LinearLayout.LayoutParams params() {
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        p.topMargin = dp(4);
        return p;
    }

    private int dp(int v) {
        return Math.round(v * getResources().getDisplayMetrics().density);
    }

    private void status(String s) {
        tvStatus.setText(s);
        tvStatus.setGravity(Gravity.START);
    }
}
