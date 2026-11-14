package com.pulsedesk.mobile;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

/**
 * Fires when the employee swipes the ongoing notification away. The service keeps running, so we
 * simply ask it to show the notification again - the device owner must always be able to see that
 * capture is running (and stop it from the same notification).
 */
public class NotificationDismissReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        if (!MonitorService.ACTIVE) return;
        try {
            context.startService(new Intent(context, MonitorService.class)
                    .setAction(MonitorService.ACTION_RESTORE));
        } catch (Exception e) {
            Log.w("PulseDesk", "restore: " + e.getMessage());
        }
    }
}
