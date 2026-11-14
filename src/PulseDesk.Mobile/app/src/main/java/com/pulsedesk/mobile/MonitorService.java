package com.pulsedesk.mobile;

import android.app.KeyguardManager;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.graphics.Bitmap;
import android.graphics.PixelFormat;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.PowerManager;
import android.os.SystemClock;
import android.util.DisplayMetrics;
import android.util.Log;
import android.view.WindowManager;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.TimeZone;

/**
 * Foreground service that mirrors the screen (MediaProjection), uploads periodic JPEG
 * screenshots, streams live frames while an admin is watching, reports the foreground
 * app + idle time, and polls the server for pause / resume / shot-now commands.
 */
public class MonitorService extends Service {
    private static final String TAG = "PulseDesk";
    private static final String CHANNEL = "pulsedesk";
    private static final int NOTIFICATION_ID = 42;
    public static final String ACTION_STOP = "com.pulsedesk.mobile.STOP";
    public static final String ACTION_RESTORE = "com.pulsedesk.mobile.RESTORE";
    /** True while the foreground service is actually capturing (used by receivers). */
    public static volatile boolean ACTIVE = false;

    private HandlerThread captureThread;
    private Handler handler;
    private MediaProjection projection;
    private MediaProjection.Callback projectionCallback;
    private VirtualDisplay display;
    private ImageReader reader;

    private String server, token;
    private volatile boolean running = true;
    private volatile boolean paused = false;
    private volatile boolean streaming = false;
    private volatile boolean shotNow = false;

    private long intervalMs = 300_000;
    private long lastShotAt = 0;
    private long lastEncodeAt = 0;
    private long lastHeartbeat = 0;
    private long lastActivityPost = 0;
    private long lastPoll = 0;
    private long screenOffAt = 0;

    private int width, height, density;

    @Override
    public void onCreate() {
        super.onCreate();
        createChannel();
        captureThread = new HandlerThread("pulsedesk-capture");
        captureThread.start();
        handler = new Handler(captureThread.getLooper());
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && ACTION_STOP.equals(intent.getAction())) {
            stopEverything();
            return START_NOT_STICKY;
        }
        if (intent != null && ACTION_RESTORE.equals(intent.getAction())) {
            // User swiped the ongoing notification away - put it back straight away so the
            // device owner can always see that capture is running.
            if (ACTIVE) refreshNotification();
            return START_STICKY;
        }
        startInForeground();

        server = Prefs.server(this);
        token = Prefs.token(this);
        if (server.isEmpty() || token.isEmpty()) {
            Log.w(TAG, "no enrollment, stopping");
            stopEverything();
            return START_NOT_STICKY;
        }

        int resultCode = intent != null ? intent.getIntExtra("resultCode", 0) : 0;
        Intent data = intent != null ? (Intent) intent.getParcelableExtra("data") : null;
        if (resultCode == 0 || data == null) {
            Log.w(TAG, "no projection permission, stopping");
            stopEverything();
            return START_NOT_STICKY;
        }

        MediaProjectionManager mgr = (MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE);
        projection = mgr.getMediaProjection(resultCode, data);
        if (projection == null) {
            stopEverything();
            return START_NOT_STICKY;
        }

        projectionCallback = new MediaProjection.Callback() {
            @Override public void onStop() {
                Log.i(TAG, "projection stopped by system/user");
                stopEverything();
            }
        };
        projection.registerCallback(projectionCallback, handler);

        startCapture();
        schedulePoll();
        return START_STICKY;
    }

    // ---- capture -----------------------------------------------------------
    private void startCapture() {
        WindowManager wm = (WindowManager) getSystemService(WINDOW_SERVICE);
        DisplayMetrics m = new DisplayMetrics();
        wm.getDefaultDisplay().getRealMetrics(m);
        density = m.densityDpi;

        int maxW = 1280;
        double scale = m.widthPixels > maxW ? maxW / (double) m.widthPixels : 1.0;
        width = ((int) Math.round(m.widthPixels * scale)) & ~1;
        height = ((int) Math.round(m.heightPixels * scale)) & ~1;
        if (width < 2 || height < 2) { width = 720; height = 1280; }

        reader = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, 2);
        reader.setOnImageAvailableListener(this::onFrame, handler);

        display = projection.createVirtualDisplay(
                "pulsedesk", width, height, density,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                reader.getSurface(), null, handler);

        Log.i(TAG, "capture started " + width + "x" + height);
        ACTIVE = true;
    }

    private void onFrame(ImageReader r) {
        Image image;
        try { image = r.acquireLatestImage(); } catch (Exception e) { return; }
        if (image == null) return;

        long now = SystemClock.elapsedRealtime();
        try {
            if (paused) return;                        // admin ne pause kiya hua hai
            boolean wantShot = shotNow || (!streaming && now - lastShotAt >= intervalMs);
            boolean wantFrame = streaming;
            if (!wantShot && !wantFrame) return;
            if (now - lastEncodeAt < 700) return;      // ~1.4 fps max, keeps CPU/battery sane
            lastEncodeAt = now;

            byte[] jpeg = toJpeg(image, streaming ? 55 : 70);
            if (jpeg == null) return;

            if (wantFrame) {
                try {
                    Api.postBytes(server + "/api/agent/frame", token, "image/jpeg", jpeg);
                } catch (Exception e) { Log.w(TAG, "frame: " + e.getMessage()); }
            }
            if (wantShot) {
                shotNow = false;
                lastShotAt = now;
                String name = new SimpleDateFormat("yyyyMMdd_HHmmss", Locale.US).format(new Date()) + ".jpg";
                try {
                    Api.postMultipart(server + "/api/shot", token, jpeg, name);
                    Log.i(TAG, "screenshot uploaded (" + jpeg.length / 1024 + " KB)");
                } catch (Exception e) { Log.w(TAG, "shot: " + e.getMessage()); }
            }
        } finally {
            try { image.close(); } catch (Exception ignored) { }
        }
    }

    private byte[] toJpeg(Image image, int quality) {
        try {
            Image.Plane plane = image.getPlanes()[0];
            ByteBuffer buffer = plane.getBuffer();
            int pixelStride = plane.getPixelStride();
            int rowStride = plane.getRowStride();
            int pad = rowStride - pixelStride * image.getWidth();

            Bitmap raw = Bitmap.createBitmap(image.getWidth() + pad / pixelStride,
                    image.getHeight(), Bitmap.Config.ARGB_8888);
            raw.copyPixelsFromBuffer(buffer);
            Bitmap out = raw;
            if (pad > 0) {
                out = Bitmap.createBitmap(raw, 0, 0, image.getWidth(), image.getHeight());
                raw.recycle();
            }
            ByteArrayOutputStream bos = new ByteArrayOutputStream();
            out.compress(Bitmap.CompressFormat.JPEG, quality, bos);
            out.recycle();
            return bos.toByteArray();
        } catch (Throwable t) {
            Log.w(TAG, "encode: " + t.getMessage());
            return null;
        }
    }

    // ---- server loops -----------------------------------------------------
    private void schedulePoll() {
        handler.postDelayed(new Runnable() {
            @Override public void run() {
                if (!running) return;
                pollOnce();
                handler.postDelayed(this, 3000);
            }
        }, 1500);
    }

    private void pollOnce() {
        try {
            String res = Api.get(server + "/api/agent/poll", token);
            JSONObject j = new JSONObject(res);
            boolean wasPaused = paused;
            paused = j.optBoolean("paused");
            if (wasPaused != paused) refreshNotification();
            streaming = j.optBoolean("streaming");
            if (j.optBoolean("shotNow")) shotNow = true;
            intervalMs = Math.max(15, j.optInt("intervalSec", 300)) * 1000L;
        } catch (Exception e) {
            Log.w(TAG, "poll: " + e.getMessage());
        }

        long now = SystemClock.elapsedRealtime();
        if (now - lastHeartbeat >= 20_000) {
            lastHeartbeat = now;
            try { Api.postJson(server + "/api/hb", token,
                    new JSONObject().put("paused", paused).toString()); }
            catch (Exception ignored) { }
        }
        if (now - lastActivityPost >= 15_000) {
            lastActivityPost = now;
            if (!paused) postActivity();
        }
    }

    private void postActivity() {
        try {
            JSONObject sample = new JSONObject();
            sample.put("ts", isoNow());
            sample.put("process", foregroundPackage());
            sample.put("title", "");
            sample.put("idle", idleSeconds());
            sample.put("active", isInteractive());
            JSONArray samples = new JSONArray().put(sample);
            Api.postJson(server + "/api/activity", token,
                    new JSONObject().put("samples", samples).toString());
        } catch (Exception e) {
            Log.w(TAG, "activity: " + e.getMessage());
        }
    }

    private String foregroundPackage() {
        try {
            android.app.usage.UsageStatsManager usm =
                    (android.app.usage.UsageStatsManager) getSystemService(Context.USAGE_STATS_SERVICE);
            long now = System.currentTimeMillis();
            android.app.usage.UsageEvents ev = usm.queryEvents(now - 60_000, now);
            android.app.usage.UsageEvents.Event e = new android.app.usage.UsageEvents.Event();
            String pkg = null;
            while (ev.hasNextEvent()) {
                ev.getNextEvent(e);
                if (e.getEventType() == android.app.usage.UsageEvents.Event.MOVE_TO_FOREGROUND)
                    pkg = e.getPackageName();
            }
            return pkg == null ? "android" : pkg;
        } catch (Exception e) {
            return "android";
        }
    }

    private boolean isInteractive() {
        PowerManager pm = (PowerManager) getSystemService(POWER_SERVICE);
        KeyguardManager km = (KeyguardManager) getSystemService(KEYGUARD_SERVICE);
        return pm.isInteractive() && !km.isKeyguardLocked();
    }

    private int idleSeconds() {
        if (isInteractive()) { screenOffAt = 0; return 0; }
        long now = SystemClock.elapsedRealtime();
        if (screenOffAt == 0) screenOffAt = now;
        return (int) ((now - screenOffAt) / 1000);
    }

    private static String isoNow() {
        SimpleDateFormat f = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss'Z'", Locale.US);
        f.setTimeZone(TimeZone.getTimeZone("UTC"));
        return f.format(new Date());
    }

    // ---- foreground notification ------------------------------------------
    private static final long ACTIVATED_AT = System.currentTimeMillis();

    private void createChannel() {
        if (Build.VERSION.SDK_INT < 26) return;
        NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        NotificationChannel ch = new NotificationChannel(CHANNEL,
                getString(R.string.channel_name), NotificationManager.IMPORTANCE_LOW);
        ch.setDescription(getString(R.string.channel_desc));
        ch.setShowBadge(false);
        ch.setLockscreenVisibility(Notification.VISIBILITY_PUBLIC);
        nm.createNotificationChannel(ch);
    }

    private Notification buildNotification() {
        Intent stop = new Intent(this, MonitorService.class).setAction(ACTION_STOP);
        PendingIntent stopPi = PendingIntent.getService(this, 1, stop,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        Intent open = new Intent(this, MainActivity.class);
        PendingIntent openPi = PendingIntent.getActivity(this, 2, open,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        Intent restore = new Intent(this, NotificationDismissReceiver.class);
        PendingIntent restorePi = PendingIntent.getBroadcast(this, 3, restore,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

        Notification.Builder b = Build.VERSION.SDK_INT >= 26
                ? new Notification.Builder(this, CHANNEL)
                : new Notification.Builder(this);
        return b.setContentTitle(getString(R.string.notif_title))
                .setContentText(getString(paused ? R.string.notif_paused : R.string.notif_active))
                .setSubText(getString(R.string.notif_subtext))
                .setStyle(new Notification.BigTextStyle().bigText(getString(R.string.notif_big)))
                .setSmallIcon(R.drawable.ic_stat_pd)
                .setColor(0xFFE0645C)
                .setCategory(Notification.CATEGORY_SERVICE)
                .setVisibility(Notification.VISIBILITY_PUBLIC)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setShowWhen(true)
                .setWhen(ACTIVATED_AT)
                .setContentIntent(openPi)
                .setDeleteIntent(restorePi)
                .addAction(new Notification.Action.Builder(
                        android.R.drawable.ic_menu_close_clear_cancel,
                        getString(R.string.notif_stop), stopPi).build())
                .build();
    }

    /** Keeps the running notification honest when the admin pauses / resumes. */
    private void refreshNotification() {
        try {
            NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            if (nm != null) nm.notify(NOTIFICATION_ID, buildNotification());
        } catch (Exception ignored) { }
    }

    private void startInForeground() {
        Notification n = buildNotification();
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(NOTIFICATION_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
        } else {
            startForeground(NOTIFICATION_ID, n);
        }
    }

    private void stopEverything() {
        running = false;
        ACTIVE = false;
        try { if (display != null) display.release(); } catch (Exception ignored) { }
        try { if (reader != null) reader.close(); } catch (Exception ignored) { }
        try {
            if (projection != null) {
                if (projectionCallback != null) projection.unregisterCallback(projectionCallback);
                projection.stop();
            }
        } catch (Exception ignored) { }
        projection = null;
        stopForeground(true);
        stopSelf();
    }

    @Override
    public void onDestroy() {
        running = false;
        ACTIVE = false;
        try { if (display != null) display.release(); } catch (Exception ignored) { }
        try { if (reader != null) reader.close(); } catch (Exception ignored) { }
        if (captureThread != null) captureThread.quitSafely();
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
