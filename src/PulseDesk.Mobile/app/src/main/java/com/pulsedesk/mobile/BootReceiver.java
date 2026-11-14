package com.pulsedesk.mobile;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.os.Build;

/**
 * After a phone reboot Android forgets the screen-share grant, so the agent cannot resume by
 * itself. Instead of silently dying we show one plain notification asking the employee to tap and
 * resume - no invite code or re-enrollment needed.
 */
public class BootReceiver extends BroadcastReceiver {
    private static final String CHANNEL = "pulsedesk";
    private static final int ID = 43;

    @Override
    public void onReceive(Context context, Intent intent) {
        String action = intent != null ? intent.getAction() : null;
        if (!Intent.ACTION_BOOT_COMPLETED.equals(action)) return;
        if (MonitorService.ACTIVE) return;
        if (Prefs.server(context).isEmpty() || Prefs.token(context).isEmpty()) return;

        try {
            NotificationManager nm = (NotificationManager) context.getSystemService(Context.NOTIFICATION_SERVICE);
            if (nm == null) return;

            if (Build.VERSION.SDK_INT >= 26) {
                NotificationChannel ch = new NotificationChannel(CHANNEL,
                        context.getString(R.string.channel_name), NotificationManager.IMPORTANCE_LOW);
                ch.setDescription(context.getString(R.string.channel_desc));
                ch.setShowBadge(false);
                nm.createNotificationChannel(ch);
            }

            Intent open = new Intent(context, MainActivity.class)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            PendingIntent pi = PendingIntent.getActivity(context, 4, open,
                    PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

            Notification.Builder b = Build.VERSION.SDK_INT >= 26
                    ? new Notification.Builder(context, CHANNEL)
                    : new Notification.Builder(context);
            nm.notify(ID, b.setContentTitle(context.getString(R.string.notif_title))
                    .setContentText(context.getString(R.string.notif_resume))
                    .setSmallIcon(R.drawable.ic_stat_pd)
                    .setColor(0xFFE0645C)
                    .setAutoCancel(true)
                    .setContentIntent(pi)
                    .build());
        } catch (Exception ignored) { }
    }
}
